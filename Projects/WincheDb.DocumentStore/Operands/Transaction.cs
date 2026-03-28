using WincheDb.DocumentStore.Operations;
using Npgsql;
using System.Text.Json.Nodes;
using WincheDb.DocumentStore.Models;
using WincheDb.Core.Models;
using WincheDb.Core.Ast;
using WincheDb.DocumentStore.Infrastructure;

namespace WincheDb.DocumentStore.Operands;

public class Transaction : IAsyncDisposable
{
    public readonly string Id;
    private readonly StoreOptions _options;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly DateTime _createdAt;
    private long _lastActivityAtTicks;
    private bool _isCompleted;

    public DateTime CreatedAt => _createdAt;
    public DateTime LastActivityAt => new(Interlocked.Read(ref _lastActivityAtTicks), DateTimeKind.Utc);
    public bool IsCompleted => _isCompleted;

    internal Transaction(string id, StoreOptions options, NpgsqlConnection connection, NpgsqlTransaction transaction)
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

    public async Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        Touch();
        var document = await GetUnprotectedAsync(path, ct);
        await AuthorizeAsync(AccessOperation.Read, path: path, getExisting: _ => Task.FromResult(document), ct: ct);
        return document;
    }

    public async Task<Document> SetAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        Touch();
        await AuthorizeAsync(AccessOperation.Write, path: path, incomingData: data, getExisting: p => GetUnprotectedAsync(p, ct), ct: ct);
        return await SetUnprotectedAsync(path, data, ct);
    }

    public async Task<Document?> UpdateAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        Touch();
        await AuthorizeAsync(AccessOperation.Write, path: path, incomingData: data, getExisting: p => GetUnprotectedAsync(p, ct), ct: ct);
        return await UpdateUnprotectedAsync(path, data, ct);
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        Touch();
        await AuthorizeAsync(AccessOperation.Delete, path: path, getExisting: p => GetUnprotectedAsync(p, ct), ct: ct);
        return await DeleteUnprotectedAsync(path, ct);
    }

    public async Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default)
    {
        Touch();
        await AuthorizeAsync(AccessOperation.Read, query: query, ct: ct);
        return await QueryUnprotectedAsync(query, ct);
    }

    #region Unprotected — bypass access rules

    public Task<Document?> GetUnprotectedAsync(string path, CancellationToken ct = default)
    {
        Touch();
        return new GetOperation(_connection, _transaction, _options.TableName)
            .ExecuteAsync(path, ct);
    }

    public Task<Document> SetUnprotectedAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        Touch();
        return new SetOperation(_connection, _transaction, _options.TableName)
            .ExecuteAsync(path, data, ct);
    }

    public Task<Document?> UpdateUnprotectedAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        Touch();
        return new UpdateOperation(_connection, _transaction, _options.TableName)
            .ExecuteAsync(path, data, ct);
    }

    public Task<bool> DeleteUnprotectedAsync(string path, CancellationToken ct = default)
    {
        Touch();
        return new DeleteOperation(_connection, _transaction, _options.TableName)
            .ExecuteAsync(path, ct);
    }

    public Task<QueryResult> QueryUnprotectedAsync(Query query, CancellationToken ct = default)
    {
        Touch();
        return new QueryOperation(_connection, _transaction, _options.TableName)
            .ExecuteAsync(query, ct);
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

    private async Task AuthorizeAsync(AccessOperation operation, string? path = null, Query? query = null, JsonObject? incomingData = null, Func<string, Task<Document?>>? getExisting = null, CancellationToken ct = default)
    {
        await AccessRuleEvaluator.EvaluateAsync(_options, operation, path, query, incomingData, getExisting, ct);
    }

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
