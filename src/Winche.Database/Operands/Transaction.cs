using Npgsql;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Winche.Database.Infrastructure;
using Winche.Database.Operations;
using Winche.Sentinel.Interfaces;
using Winche.Sentinel.Models;
using Winche.Database.AST.Models;
using Winche.Database.Core.Models;
using Winche.Database.Models;

namespace Winche.Database.Operands;

public class Transaction : IAsyncDisposable
{
    public readonly string Id;
    private readonly string _table;
    private readonly IAccessRuleEvaluator<Document> _evaluator;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly DateTime _createdAt;
    private long _lastActivityAtTicks;
    private bool _isCompleted;

    public DateTime CreatedAt => _createdAt;
    public DateTime LastActivityAt => new(Interlocked.Read(ref _lastActivityAtTicks), DateTimeKind.Utc);
    public bool IsCompleted => _isCompleted;

    internal Transaction(string id, IOptions<StoreOptions> options, IAccessRuleEvaluator<Document> evaluator, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Id = id;
        _table = options.Value.TableName;
        _evaluator = evaluator;
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
        await _evaluator.EvaluateAsync(AccessOperation.Read, path, null, (ct) => GetResourceAsync(path, ct), ct);
        return await GetUnprotectedAsync(path, ct); ;
    }

    public async Task<Document> SetAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        Touch();
        await _evaluator.EvaluateAsync(AccessOperation.Write, path, data, (ct) => GetResourceAsync(path, ct), ct);
        return await SetUnprotectedAsync(path, data, ct);
    }

    public async Task<Document?> UpdateAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        Touch();
        await _evaluator.EvaluateAsync(AccessOperation.Write, path, data, (ct) => GetResourceAsync(path, ct), ct);
        return await UpdateUnprotectedAsync(path, data, ct);
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        Touch();

        var op = new DeleteOperation(_connection, _transaction, _table);

        var authorizedPaths = await op.SelectForUpdateAsync(path, ct);
        var authorizedSet = new HashSet<string>(authorizedPaths);

        foreach (var p in authorizedPaths)
            await _evaluator.EvaluateAsync(AccessOperation.Delete, p, null, (ct) => GetResourceAsync(p, ct), ct);

        var deleted = await op.ExecuteAsync(path, ct);

        var stowaways = deleted.Where(p => !authorizedSet.Contains(p)).ToList();
        if (stowaways.Count > 0)
        {
            await _transaction.RollbackAsync(ct);
            _isCompleted = true;
            throw new ConcurrentSubtreeModificationException(path, stowaways);
        }

        return deleted.Count > 0;
    }

    public async Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default)
    {
        Touch();
        var result = await QueryUnprotectedAsync(query, ct);

        var allowed = new List<Document>(result.Documents.Count);
        foreach (var doc in result.Documents)
        {
            try
            {
                await _evaluator.EvaluateAsync(AccessOperation.Read, doc.Path, null, _ => Task.FromResult<Document?>(doc), ct);
                allowed.Add(doc);
            }
            catch (AccessDeniedException) { }
            catch (NoRulesMatchedException) { }
        }

        return result with { Documents = allowed };
    }

    #region Unprotected — bypass access rules

    public Task<Document?> GetUnprotectedAsync(string path, CancellationToken ct = default)
    {
        Touch();
        return new GetOperation(_connection, _transaction, _table).ExecuteAsync(path, ct);
    }

    public Task<Document> SetUnprotectedAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        Touch();
        return new SetOperation(_connection, _transaction, _table).ExecuteAsync(path, data, ct);
    }

    public Task<Document?> UpdateUnprotectedAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        Touch();
        return new UpdateOperation(_connection, _transaction, _table).ExecuteAsync(path, data, ct);
    }

    public async Task<bool> DeleteUnprotectedAsync(string path, CancellationToken ct = default)
    {
        Touch();
        var deleted = await new DeleteOperation(_connection, _transaction, _table).ExecuteAsync(path, ct);
        return deleted.Count > 0;
    }

    public Task<QueryResult> QueryUnprotectedAsync(Query query, CancellationToken ct = default)
    {
        Touch();
        return new QueryOperation(_connection, _transaction, _table).ExecuteAsync(query, ct);
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

    #region Resource Access

    private async Task<Document?> GetResourceAsync(string path, CancellationToken ct = default)
    {
        return await new GetOperation(_connection, _transaction, _table).ExecuteAsync(path, ct);
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
