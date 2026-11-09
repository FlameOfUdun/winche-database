using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Winche.Database.Runtime.Coordination;

/// <summary>
/// A session-level Postgres advisory lock, held on a dedicated connection for as long as the lease lives.
/// Used so that work which must run on exactly one node (the durable hook runner, schema setup) does,
/// however many nodes share the database. If the holder dies, Postgres releases the lock with its
/// connection and another node can take it.
///
/// <see cref="Lost"/> is cancelled when the lock's connection fails (checked every heartbeat), so the
/// holder can stop work it no longer exclusively owns.
/// </summary>
public sealed class AdvisoryLease : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly long _key;
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _heartbeat;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AdvisoryLease(NpgsqlConnection connection, long key, TimeSpan heartbeat)
    {
        _connection = connection;
        _key = key;
        _heartbeat = HeartbeatAsync(heartbeat);
    }

    /// <summary>Cancelled when the lock can no longer be guaranteed (its connection failed).</summary>
    public CancellationToken Lost => _lost.Token;

    /// <summary>The 64-bit lock key for <paramref name="name"/> (stable across processes and versions).</summary>
    public static long KeyFor(string name) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("winche:" + name)), 0);

    /// <summary>Takes the lock if it is free; null when another session holds it.</summary>
    public static Task<AdvisoryLease?> TryAcquireAsync(NpgsqlDataSource source, string name, TimeSpan heartbeat, CancellationToken ct = default) =>
        AcquireCoreAsync(source, name, heartbeat, wait: false, ct);

    /// <summary>Waits until the lock is free and takes it.</summary>
    public static async Task<AdvisoryLease> AcquireAsync(NpgsqlDataSource source, string name, TimeSpan heartbeat, CancellationToken ct = default) =>
        (await AcquireCoreAsync(source, name, heartbeat, wait: true, ct))!;

    private static async Task<AdvisoryLease?> AcquireCoreAsync(NpgsqlDataSource source, string name, TimeSpan heartbeat, bool wait, CancellationToken ct)
    {
        var key = KeyFor(name);
        var connection = source.CreateConnection();
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = wait ? "SELECT pg_advisory_lock(@key)" : "SELECT pg_try_advisory_lock(@key)";
            command.Parameters.AddWithValue("key", key);
            var result = await command.ExecuteScalarAsync(ct);
            if (!wait && result is not true)
            {
                await connection.DisposeAsync();
                return null;
            }
            return new AdvisoryLease(connection, key, heartbeat);
        }
        catch
        {
            // A connection that may hold the lock must not go back to the pool still holding it.
            NpgsqlConnection.ClearPool(connection);
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task HeartbeatAsync(TimeSpan interval)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, _stop.Token);
                await _gate.WaitAsync(_stop.Token);
                try
                {
                    await using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT 1";
                    await command.ExecuteScalarAsync(_stop.Token);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch
        {
            // The connection is gone, and with it the lock.
            _lost.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _heartbeat; } catch { /* ended */ }

        var unlocked = false;
        if (!_lost.IsCancellationRequested)
        {
            try
            {
                await using var command = _connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@key)";
                command.Parameters.AddWithValue("key", _key);
                await command.ExecuteScalarAsync();
                unlocked = true;
            }
            catch
            {
                // Fall through: the connection is discarded below, which releases the lock.
            }
        }

        // A pooled connection keeps session-level locks; never return one that may still hold it.
        if (!unlocked) NpgsqlConnection.ClearPool(_connection);
        await _connection.DisposeAsync();
        _lost.Dispose();
        _stop.Dispose();
        _gate.Dispose();
    }
}
