using WincheDb.DocumentStore.Operations;
using Npgsql;
using System.Text.Json.Nodes;
using WincheDb.DocumentStore.Models;
using WincheDb.Core.Models;
using WincheDb.Core.Ast;

namespace WincheDb.DocumentStore.Operands;

public class StoreTransaction : IAsyncDisposable
{
    public readonly string Id;
    private readonly StoreOptions _options;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly DateTime _createdAt;
    private long _lastActivityAtTicks;
    private bool _isCompleted;

    public DateTime CreatedAt => _createdAt;
    public DateTime LastActivityAt => new DateTime(Interlocked.Read(ref _lastActivityAtTicks), DateTimeKind.Utc);
    public bool IsCompleted => _isCompleted;

    internal StoreTransaction(string id, StoreOptions options, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Id = id;
        _options = options;
        _connection = connection;
        _transaction = transaction;
        _createdAt = DateTime.UtcNow;
        _lastActivityAtTicks = DateTime.UtcNow.Ticks;
    }

    private void Touch()
    {
        Interlocked.Exchange(ref _lastActivityAtTicks, DateTime.UtcNow.Ticks);
    }

    #region Read Operations

    public Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        Touch();
        return new GetOperation(_connection, _transaction, _options.TableName)
            .ExecuteAsync(path, ct);
    }

    public Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default)
    {
        Touch();
        return new QueryOperation(_connection, _transaction, _options.TableName)
            .ExecuteAsync(query, ct);
    }

    #endregion

    #region Write Operations

    public Task<Document> SetAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        Touch();
        return new SetOperation(_connection, _transaction, _options.TableName)
            .ExecuteAsync(path, data, ct);
    }

    public Task<Document?> UpdateAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        Touch();
        return new UpdateOperation(_connection, _transaction, _options.TableName)
            .ExecuteAsync(path, data, ct);
    }

    public Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        Touch();
        return new DeleteOperation(_connection, _transaction, _options.TableName)
            .ExecuteAsync(path, ct);
    }

    #endregion

    #region Transaction Control

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_isCompleted)
        {
            return;
        }
        await _transaction.CommitAsync(ct);
        _isCompleted = true;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_isCompleted)
        {
            return;
        }
        await _transaction.RollbackAsync(ct);
        _isCompleted = true;
    }

    #endregion

    #region Dispose

    public async ValueTask DisposeAsync()
    {
        if (!_isCompleted)
        {
            try
            {
                await _transaction.RollbackAsync();
            }
            catch
            {
                // Ignore rollback errors during dispose
            }
        }

        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    #endregion
}
