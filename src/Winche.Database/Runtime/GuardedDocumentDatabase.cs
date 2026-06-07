using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Sentinel.Interfaces;
using Winche.Sentinel.Models;

namespace Winche.Database.Runtime;

/// <summary>
/// Sentinel access-rule decorator over the rule-free core (spec §5). The core IS the old
/// "unprotected" API; inject the core directly where rules must be bypassed.
/// Cascade deletes are guarded at the ROOT path only (documented simplification).
/// </summary>
public sealed class GuardedDocumentDatabase(IDocumentDatabase inner, IAccessRuleEvaluator<Document> evaluator)
    : IDocumentDatabase
{
    // ── Reads ─────────────────────────────────────────────────────────────────

    public async Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        // fetch once; rules that inspect the resource get the same snapshot we return
        var doc = await inner.GetAsync(path, ct);
        await evaluator.EvaluateAsync(AccessOperation.Read, path, null, _ => Task.FromResult(doc), ct);
        return doc;
    }

    public async Task<IReadOnlyList<Document?>> GetAllAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var docs = await inner.GetAllAsync(paths, ct);
        for (var i = 0; i < paths.Count; i++)
        {
            var doc = docs[i];
            await evaluator.EvaluateAsync(AccessOperation.Read, paths[i], null, _ => Task.FromResult(doc), ct);
        }
        return docs;
    }

    public async Task<QueryResult> QueryAsync(QueryAst query, CancellationToken ct = default)
    {
        var result = await inner.QueryAsync(query, ct);
        return result with { Documents = await FilterReadable(result.Documents, ct) };
    }

    public async Task<PipelineResult> AggregateAsync(PipelineAst pipeline, CancellationToken ct = default)
    {
        foreach (var stage in pipeline.Stages)
        {
            var collection = stage switch
            {
                MatchStageAst m => m.Collection,
                LookupStageAst l => l.Collection,
                _ => null,
            };
            if (collection is not null)
                await evaluator.EvaluateAsync(AccessOperation.Read, collection, null,
                    _ => Task.FromResult<Document?>(null), ct);
        }
        return await inner.AggregateAsync(pipeline, ct);
    }

    // ── Writes ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<Write> writes, CancellationToken ct = default)
    {
        await GuardWrites(writes, ct);
        return await inner.WriteAsync(writes, ct);
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    public Task<TransactionHandle> BeginTransactionAsync(CancellationToken ct = default) =>
        inner.BeginTransactionAsync(ct);

    public async Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default)
    {
        await evaluator.EvaluateAsync(AccessOperation.Read, path, null, c => inner.GetAsync(path, c), ct);
        return await inner.GetAsync(transactionId, path, ct);
    }

    public async Task<QueryResult> QueryAsync(string transactionId, QueryAst query, CancellationToken ct = default)
    {
        var result = await inner.QueryAsync(transactionId, query, ct);
        return result with { Documents = await FilterReadable(result.Documents, ct) };
    }

    public async Task<IReadOnlyList<WriteResult>> CommitTransactionAsync(
        string transactionId, IReadOnlyList<Write> writes, CancellationToken ct = default)
    {
        await GuardWrites(writes, ct);
        return await inner.CommitTransactionAsync(transactionId, writes, ct);
    }

    public Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default) =>
        inner.RollbackTransactionAsync(transactionId, ct);

    public Task<T> RunTransactionAsync<T>(Func<TransactionContext, Task<T>> body,
        TransactionOptions? options = null, CancellationToken ct = default) =>
        TransactionRunner.RunAsync(this, body, options, ct);          // context reads/commits go through THIS guard

    public IQueryListener Listen(QueryAst query, ListenOptions? options = null) =>
        new GuardedQueryListener(inner.Listen(query, options), evaluator);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task GuardWrites(IReadOnlyList<Write> writes, CancellationToken ct)
    {
        foreach (var write in writes)
        {
            var (op, data) = write switch
            {
                SetWrite s => (AccessOperation.Write, (object?)WireData(s.Fields)),
                UpdateWrite u => (AccessOperation.Write, (object?)WireData(u.Fields)),
                DeleteWrite => (AccessOperation.Delete, (object?)null),
                _ => (AccessOperation.Write, (object?)null),
            };
            await evaluator.EvaluateAsync(op, write.Path, data, c => inner.GetAsync(write.Path, c), ct);
        }
    }

    private static JsonObject? WireData(IReadOnlyDictionary<string, Value> fields) =>
        ValueSerializer.WriteFields(fields.Where(kv => kv.Value is not DeleteFieldValue)
            .ToDictionary(kv => kv.Key, kv => kv.Value));

    /// <summary>
    /// Update guard data is DOT-KEYED ({"a.b": …}), not nested — rule authors inspecting
    /// update payloads must match the literal dotted field paths (set payloads ARE nested).
    /// </summary>
    private static JsonObject? WireData(IReadOnlyDictionary<FieldPath, Value> fields)
    {
        var obj = new JsonObject();
        foreach (var (path, value) in fields)
            if (value is not DeleteFieldValue)
                obj[path.ToString()] = ValueSerializer.Write(value);
        return obj;
    }

    private async Task<IReadOnlyList<Document>> FilterReadable(IReadOnlyList<Document> docs, CancellationToken ct)
    {
        var allowed = new List<Document>(docs.Count);
        foreach (var doc in docs)
        {
            try
            {
                await evaluator.EvaluateAsync(AccessOperation.Read, doc.Path, null,
                    _ => Task.FromResult<Document?>(doc), ct);
                allowed.Add(doc);
            }
            catch (AccessDeniedException) { }
            catch (NoRulesMatchedException) { }
        }
        return allowed;
    }
}
