using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Matching;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Querying;

namespace Winche.Database.Authorization;

/// <summary>
/// Winche.Rules-based authorization decorator over the rule-free core <see cref="IDocumentDatabase"/>.
/// Replaces Sentinel-based get/write authorization with the Firestore-parity rules engine.
/// This guard is NOT wired through DI in Phase 1 — it is constructed directly where needed.
/// </summary>
/// <remarks>
/// Constructor arguments are intentionally decoupled from the transport layer:
/// <paramref name="claimsProvider"/> is a <see cref="Func{T}"/> so the caller controls how auth
/// claims are sourced (HTTP context, test fixture, etc.).
/// </remarks>
public sealed class RuleGuardedDocumentDatabase(
    IDocumentDatabase inner,
    Ruleset ruleset,
    Func<IReadOnlyDictionary<string, object?>?> claimsProvider)
    : IDocumentDatabase
{
    // ── Reads ─────────────────────────────────────────────────────────────────

    public async Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        var doc = await inner.GetAsync(path, ct);
        await AuthorizeGetAsync(path, doc, ct);
        return doc;
    }

    public async Task<IReadOnlyList<Document?>> GetAllAsync(
        IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        // Authorize each path individually as a get. Load via inner to get current document state
        // for resource, then check rules. Any single denial throws before returning results.
        var documents = new PostgresDocumentSource(inner);
        var claims = claimsProvider();
        foreach (var path in paths)
        {
            var doc = await inner.GetAsync(path, ct);
            var resource = doc is not null ? DocumentToResource.Convert(doc) : RuleValue.Null;
            var ctx = new RuleContext
            {
                Resource = resource,
                Request = RequestBuilder.Build(claims, "get", RuleValue.Null),
                Documents = documents,
                Params = RuleContext.NoParams,
            };
            if (!await RulesetEvaluator.AllowsAsync(ruleset, RuleOperation.Get, path, ctx, ct))
                throw new AccessDeniedException(path, "get");
        }
        return await inner.GetAllAsync(paths, ct);
    }

    public async Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default)
    {
        AuthorizeListQuery(query);
        return await inner.QueryAsync(query, ct);
    }

    public async Task<long> CountAsync(Query query, CancellationToken ct = default)
    {
        AuthorizeListQuery(query);
        return await inner.CountAsync(query, ct);
    }

    // ── Writes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two-pass batch write: authorize ALL writes first (first pass), apply ALL or none (second pass).
    /// A single denied write rejects the entire batch before any mutation reaches the database.
    /// </summary>
    public async Task<IReadOnlyList<WriteResult>> WriteAsync(
        IReadOnlyList<Write> writes, CancellationToken ct = default)
    {
        await AuthorizeWritesAsync(writes, ct);
        return await inner.WriteAsync(writes, ct);
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    public Task<TransactionHandle> BeginTransactionAsync(CancellationToken ct = default) =>
        inner.BeginTransactionAsync(ct);

    public Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default)
    {
        // TODO Phase 2/3: authorize (transactional get)
        return inner.GetAsync(transactionId, path, ct);
    }

    public Task<QueryResult> QueryAsync(string transactionId, Query query, CancellationToken ct = default)
    {
        AuthorizeListQuery(query);
        return inner.QueryAsync(transactionId, query, ct);
    }

    public async Task<IReadOnlyList<WriteResult>> CommitTransactionAsync(
        string transactionId, IReadOnlyList<Write> writes, CancellationToken ct = default)
    {
        // TODO Phase 2/3: authorize writes inside a transaction (same two-pass pattern)
        return await inner.CommitTransactionAsync(transactionId, writes, ct);
    }

    public Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default) =>
        inner.RollbackTransactionAsync(transactionId, ct);

    public Task<T> RunTransactionAsync<T>(
        Func<TransactionContext, Task<T>> body,
        TransactionOptions? options = null,
        CancellationToken ct = default) =>
        // TODO Phase 2/3: thread authorization through TransactionRunner using this guard
        inner.RunTransactionAsync(body, options, ct);

    /// <summary>
    /// Authorizes the subscription query at subscribe time using the same
    /// <see cref="QueryAnalyzer"/>-based analyzer as <see cref="QueryAsync"/>.
    /// If the query is provably safe, every document the live stream can ever
    /// return already satisfies the read rule (the query's own constraints
    /// guarantee it), so no per-document filtering is applied to snapshots.
    /// If the query is not provably safe the subscription is rejected outright
    /// with <see cref="AccessDeniedException"/> — Firestore's "rules are not
    /// filters" contract applied to listeners.
    /// <para>
    /// <b>Limitation:</b> authorization is evaluated once against the
    /// subscribe-time claims. If the caller's auth token is refreshed or
    /// revoked after the listener is open the subscription is NOT re-checked.
    /// Re-authorization on token refresh is out of scope for Phase 3 and
    /// should be addressed in a future phase.
    /// </para>
    /// </summary>
    public IQueryListener Listen(Query query, ListenOptions? options = null)
    {
        AuthorizeListQuery(query);           // throws AccessDeniedException at subscribe time if not provably safe
        return inner.Listen(query, options); // provably safe → every live result is authorized; no per-doc filtering
    }

    // ── Authorization helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Authorizes a single get operation. Shared between <see cref="GetAsync(string,CancellationToken)"/>
    /// and the per-path loop in <see cref="GetAllAsync"/>.
    /// </summary>
    private async Task AuthorizeGetAsync(string path, Document? doc, CancellationToken ct)
    {
        var resource = doc is not null ? DocumentToResource.Convert(doc) : RuleValue.Null;
        var ctx = new RuleContext
        {
            Resource = resource,
            Request = RequestBuilder.Build(claimsProvider(), "get", RuleValue.Null),
            Documents = new PostgresDocumentSource(inner),
            Params = RuleContext.NoParams,
        };

        if (!await RulesetEvaluator.AllowsAsync(ruleset, RuleOperation.Get, path, ctx, ct))
            throw new AccessDeniedException(path, "get");
    }

    /// <summary>
    /// Authorizes a list/query operation using <see cref="QueryAnalyzer.Allows"/>.
    /// Implements Firestore's "rules are not filters": the query is either provably safe and
    /// allowed in full, or rejected outright — results are never post-filtered.
    /// </summary>
    private void AuthorizeListQuery(Query query)
    {
        var constraints = QueryToConstraints.Convert(query);
        var ctx = new RuleContext
        {
            Resource = RuleValue.Null,
            Request = RequestBuilder.Build(claimsProvider(), "list", RuleValue.Null),
            Documents = new PostgresDocumentSource(inner),
            Params = RuleContext.NoParams,
        };

        if (!QueryAnalyzer.Allows(ruleset, constraints, ctx))
            throw new AccessDeniedException(query.Collection, "list");
    }

    /// <summary>
    /// First pass of the two-pass write protocol: evaluate every write against the rules engine.
    /// Creates ONE shared <see cref="PostgresDocumentSource"/> so that intra-batch <c>get()</c> /
    /// <c>exists()</c> calls in rules share a cache and avoid redundant round-trips.
    ///
    /// <para>A single <c>now</c> timestamp is captured (truncated to microseconds, matching
    /// <see cref="TimestampValue"/>'s own µs-truncation) at the start of the batch and threaded through
    /// both <c>request.time</c> and all <c>serverTimestamp</c> transform resolutions, so the invariant
    /// <c>request.resource.data.&lt;tsField&gt; == request.time</c> holds for any transform-target field.</para>
    /// <para>Note on commit-time fidelity: the actual DB commit time is assigned by Postgres (via
    /// <c>transaction_timestamp()</c>) and will differ from this pre-flight <c>now</c>. The contract
    /// here is internal rule-evaluation consistency, not exact replica of the eventual stored value.</para>
    /// </summary>
    private async Task AuthorizeWritesAsync(IReadOnlyList<Write> writes, CancellationToken ct)
    {
        // Capture once and truncate to microseconds (matching TimestampValue's own truncation so
        // that the resolved serverTimestamp field value == request.time exactly when compared by
        // the rules engine, which uses DateTimeOffset.Equals on the raw value).
        var rawNow = DateTimeOffset.UtcNow;
        var now = new DateTimeOffset(rawNow.Ticks - rawNow.Ticks % 10, rawNow.Offset);
        var claims = claimsProvider();
        var documents = new PostgresDocumentSource(inner);

        foreach (var write in writes)
        {
            var path = write.Path;

            // Read the existing document once per write path (cached by PostgresDocumentSource if the
            // same path appears more than once in the batch, though that is unusual).
            var existing = await inner.GetAsync(path, ct);

            RuleOperation op;
            string method;
            RuleValue resource;
            RuleValue requestResource;

            switch (write)
            {
                case DeleteWrite:
                    op = RuleOperation.Delete;
                    method = "delete";
                    resource = existing is not null ? DocumentToResource.Convert(existing) : RuleValue.Null;
                    requestResource = RuleValue.Null;
                    break;

                case SetWrite setWrite:
                    op = existing is null ? RuleOperation.Create : RuleOperation.Update;
                    method = existing is null ? "create" : "update";
                    resource = existing is not null ? DocumentToResource.Convert(existing) : RuleValue.Null;
                    requestResource = DocumentToResource.Convert(
                        BuildPostWriteDocument(path, existing, setWrite, now));
                    break;

                case UpdateWrite updateWrite:
                    // UpdateWrite has an implicit exists:true precondition (WriteApplier enforces this).
                    // We treat it as Update regardless; if the doc doesn't exist the inner layer will
                    // throw NotFound — the auth check still runs so rules can inspect the absent resource.
                    op = RuleOperation.Update;
                    method = "update";
                    resource = existing is not null ? DocumentToResource.Convert(existing) : RuleValue.Null;
                    requestResource = DocumentToResource.Convert(
                        BuildPostWriteDocument(path, existing, updateWrite, now));
                    break;

                default:
                    throw new NotSupportedException($"Unknown write type: {write.GetType().Name}");
            }

            var ctx = new RuleContext
            {
                Resource = resource,
                Request = RequestBuilder.Build(claims, method, requestResource, now),
                Documents = documents,
                Params = RuleContext.NoParams,
            };

            if (!await RulesetEvaluator.AllowsAsync(ruleset, op, path, ctx, ct))
                throw new AccessDeniedException(path, method);
        }
    }

    // ── Post-write document computation ───────────────────────────────────────

    /// <summary>
    /// Builds a synthetic <see cref="Document"/> representing the state of the document AFTER
    /// <paramref name="write"/> is applied. Used to populate <c>request.resource</c> in rule conditions.
    ///
    /// <para>Mirrors <see cref="WriteApplier"/>'s C# apply pipeline exactly:
    /// <list type="number">
    ///   <item>Prune <see cref="DeleteFieldValue"/> sentinels (mirrors <c>PruneSentinels</c>).</item>
    ///   <item>Merge or replace fields (<see cref="DocumentMerger.Merge"/> / assign).</item>
    ///   <item>Apply all server-side transforms in order via <see cref="TransformApplier.Apply"/>,
    ///         using <paramref name="now"/> as the <c>serverTimestamp</c> resolution value — the same
    ///         value passed to <c>request.time</c>, so the invariant
    ///         <c>request.resource.data.&lt;tsField&gt; == request.time</c> holds.</item>
    /// </list>
    /// </para>
    /// </summary>
    private static Document BuildPostWriteDocument(string path, Document? existing, SetWrite s, DateTimeOffset now)
    {
        var pathInfo = DocumentPathParser.ParsePath(path);

        // Prune delete sentinels from the incoming fields (mirrors WriteApplier.PruneSentinels)
        // before merging, so we don't embed sentinel objects in the synthetic resource.
        var prunedData = s.Merge
            ? PruneSentinelsForResource(s.Fields)
            : s.Fields;

        IReadOnlyDictionary<string, Value> fields;
        if (s.Merge && existing is not null)
            fields = DocumentMerger.Merge(existing.Fields, prunedData);
        else
            fields = prunedData;

        // Apply server-side transforms in declaration order, exactly as WriteApplier.ApplyTransforms does.
        if (s.Transforms is { Count: > 0 } transforms)
        {
            foreach (var t in transforms)
            {
                var existing2 = FilterEvaluator.ResolveField(t.Field, fields);
                var transformed = TransformApplier.Apply(existing2, t, now);
                fields = FieldMutator.Set(fields, t.Field, transformed);
            }
        }

        return new Document
        {
            Path = path,
            Id = pathInfo.Id ?? string.Empty,
            Collection = pathInfo.Collection,
            Fields = fields,
            CreateTime = existing?.CreateTime ?? now,
            UpdateTime = now,
            Version = (existing?.Version ?? 0) + 1,
        };
    }

    /// <summary>
    /// Builds a synthetic <see cref="Document"/> representing the post-write state of an
    /// <see cref="UpdateWrite"/>. Applies each field-path mutation in order using
    /// <see cref="FieldMutator"/>, then resolves all server-side transforms via
    /// <see cref="TransformApplier.Apply"/>, matching <see cref="WriteApplier"/>'s
    /// <c>ApplyUpdate</c> pipeline exactly.
    /// </summary>
    private static Document BuildPostWriteDocument(string path, Document? existing, UpdateWrite u, DateTimeOffset now)
    {
        var pathInfo = DocumentPathParser.ParsePath(path);
        IReadOnlyDictionary<string, Value> fields = existing?.Fields
            ?? new Dictionary<string, Value>();

        foreach (var (fieldPath, value) in u.Fields)
        {
            fields = value is DeleteFieldValue
                ? FieldMutator.Delete(fields, fieldPath)
                : FieldMutator.Set(fields, fieldPath, value);
        }

        // Apply server-side transforms in declaration order, exactly as WriteApplier.ApplyTransforms does.
        if (u.Transforms is { Count: > 0 } transforms)
        {
            foreach (var t in transforms)
            {
                var existing2 = FilterEvaluator.ResolveField(t.Field, fields);
                var transformed = TransformApplier.Apply(existing2, t, now);
                fields = FieldMutator.Set(fields, t.Field, transformed);
            }
        }

        return new Document
        {
            Path = path,
            Id = pathInfo.Id ?? string.Empty,
            Collection = pathInfo.Collection,
            Fields = fields,
            CreateTime = existing?.CreateTime ?? now,
            UpdateTime = now,
            Version = (existing?.Version ?? 0) + 1,
        };
    }

    /// <summary>
    /// Strips <see cref="DeleteFieldValue"/> sentinels from a field map (shallow top-level only for
    /// resource-building purposes). Nested maps are recursed to remove sentinels at any depth.
    /// </summary>
    private static IReadOnlyDictionary<string, Value> PruneSentinelsForResource(
        IReadOnlyDictionary<string, Value> fields)
    {
        Dictionary<string, Value>? result = null;

        foreach (var (key, value) in fields)
        {
            if (value is DeleteFieldValue)
            {
                result ??= fields.ToDictionary(kv => kv.Key, kv => kv.Value);
                result.Remove(key);
            }
            else if (value is MapValue m)
            {
                var pruned = PruneSentinelsForResource(m.Fields);
                if (!ReferenceEquals(pruned, m.Fields))
                {
                    result ??= fields.ToDictionary(kv => kv.Key, kv => kv.Value);
                    result[key] = new MapValue(pruned);
                }
            }
        }

        return result ?? fields;
    }
}
