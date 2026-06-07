using Npgsql;
using Winche.Database.Core.Infrastructure;
using Winche.Database.Documents;
using Winche.Database.Querying.Matching;
using Winche.Database.Querying.Sql;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Writes;

/// <summary>
/// THE single mutation path (spec §2). One short DB transaction:
/// lock (sorted) → read-validations (ABORTED) → preconditions → apply sequentially in C#
/// → persist → changelog rows (same tx). Singles, batches and transaction commits all
/// run through ApplyAsync; readValidations is the Plan-2 transaction hook.
///
/// Atomicity argument: READ COMMITTED + the guarded upsert (versionBefore check in the
/// ON CONFLICT clause) together ensure that a concurrent creator racing on an absent row
/// causes affected-rows = 0, which is detected and turned into ABORTED before commit.
/// For rows held under FOR UPDATE the guard always passes — no behavior change.
/// </summary>
public sealed class WriteApplier(NpgsqlDataSource source, string table)
{
    private sealed class DocState
    {
        public IReadOnlyDictionary<string, Value> Fields = new Dictionary<string, Value>();
        public long Version;
        public DateTimeOffset CreatedAt;
        public DateTimeOffset UpdatedAt;
        public bool ExistedBefore;
        public bool Exists;
        public long VersionBefore;
    }

    public async Task<IReadOnlyList<WriteResult>> ApplyAsync(
        IReadOnlyList<Write> writes,
        IReadOnlyDictionary<string, DateTimeOffset?>? readValidations = null,
        CancellationToken ct = default)
    {
        if (writes.Count > 0)
            WriteValidator.Validate(writes);
        else if (readValidations is null || readValidations.Count == 0)
            throw new RuntimeException(RuntimeStatus.InvalidArgument,
                "A write batch must contain at least one write.");

        await using var conn = await source.OpenConnectionAsync(ct);
        // READ COMMITTED: atomicity relies on the guarded upsert (versionBefore WHERE clause).
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);

        try
        {
            var commitTime = await GetCommitTimeAsync(conn, tx, ct);

            // Collect exact paths: direct write paths ∪ readValidation keys ∪ cascade root paths
            // (roots are included so the root itself is locked even when it has no descendants).
            var cascadeWritePaths = writes.OfType<DeleteWrite>().Where(d => d.Cascade)
                .Select(d => d.Path).ToList();

            var exactPaths = writes.Select(w => w.Path)
                .Concat(readValidations?.Keys ?? [])
                .Concat(cascadeWritePaths)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            // ONE combined locking query — global ORDER BY path prevents deadlock.
            var state = await LoadAllLockedAsync(conn, tx, exactPaths, cascadeWritePaths, ct);

            // Compute cascade victims in C# from already-loaded state.
            var cascadeVictims = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var root in cascadeWritePaths)
            {
                cascadeVictims[root] = state.Keys
                    .Where(p => p == root || p.StartsWith(root + "/", StringComparison.Ordinal))
                    .Where(p => state[p].ExistedBefore)
                    .ToList();
            }

            if (readValidations is not null)
            {
                foreach (var (path, expected) in readValidations)
                {
                    var current = state.TryGetValue(path, out var s) && s.Exists ? s.UpdatedAt : (DateTimeOffset?)null;
                    if (current != (expected is { } e ? Truncate(e) : null))
                        throw new RuntimeException(RuntimeStatus.Aborted, $"Transaction read of '{path}' is stale.");
                }
            }

            var results = new List<WriteResult>(writes.Count);
            foreach (var write in writes)
            {
                if (!state.TryGetValue(write.Path, out var doc))
                    state[write.Path] = doc = new DocState();

                CheckPrecondition(write, doc);

                results.Add(write switch
                {
                    SetWrite s => ApplySet(doc, s, commitTime),
                    UpdateWrite u => ApplyUpdate(doc, u, commitTime),
                    DeleteWrite d => ApplyDelete(write.Path, d, state, cascadeVictims, commitTime),
                    _ => throw new NotSupportedException(write.GetType().Name),
                });
            }

            await PersistAsync(conn, tx, state, commitTime, ct);
            await tx.CommitAsync(ct);
            return results;
        }
        catch (PostgresException ex) when (ex.SqlState == "40P01")
        {
            // Defense-in-depth: deadlock detected — surface as Aborted so callers can retry.
            throw new RuntimeException(RuntimeStatus.Aborted, "Write contention (deadlock detected).");
        }
    }

    // ── Preconditions ─────────────────────────────────────────────────────────

    private static void CheckPrecondition(Write write, DocState doc)
    {
        if (write is UpdateWrite && !doc.Exists)
            throw new RuntimeException(RuntimeStatus.NotFound, $"No document to update: '{write.Path}'.");

        var p = write.Precondition;
        if (p is null) return;

        if (p.Exists == true && !doc.Exists)
            throw new RuntimeException(RuntimeStatus.NotFound, $"Document '{write.Path}' does not exist.");
        if (p.Exists == false && doc.Exists)
            throw new RuntimeException(RuntimeStatus.AlreadyExists, $"Document '{write.Path}' already exists.");
        if (p.UpdateTime is { } t && (!doc.Exists || doc.UpdatedAt != Truncate(t)))
            throw new RuntimeException(RuntimeStatus.FailedPrecondition,
                $"updateTime precondition failed for '{write.Path}'.");
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    private static WriteResult ApplySet(DocState doc, SetWrite s, DateTimeOffset commitTime)
    {
        var sentinelKeys = s.Fields.Where(kv => kv.Value is DeleteFieldValue).Select(kv => kv.Key).ToList();
        var data = s.Fields.Where(kv => kv.Value is not DeleteFieldValue)
                           .ToDictionary(kv => kv.Key, kv => kv.Value);

        IReadOnlyDictionary<string, Value> fields = s.Merge && doc.Exists
            ? DocumentMerger.Merge(doc.Fields, data)
            : data;

        // Sentinel keys are LITERAL top-level map keys — do NOT parse as FieldPaths.
        // merge-only (validator guarantees sentinels only appear with Merge=true)
        if (sentinelKeys.Count > 0)
        {
            var mutable = fields.ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var key in sentinelKeys) mutable.Remove(key);
            fields = mutable;
        }

        var transformResults = ApplyTransforms(ref fields, s.Transforms, commitTime);
        Bump(doc, fields, commitTime);
        return new WriteResult(commitTime, transformResults);
    }

    private static WriteResult ApplyUpdate(DocState doc, UpdateWrite u, DateTimeOffset commitTime)
    {
        var fields = doc.Fields;
        foreach (var (path, value) in u.Fields)
            fields = value is DeleteFieldValue
                ? FieldMutator.Delete(fields, path)
                : FieldMutator.Set(fields, path, value);

        var transformResults = ApplyTransforms(ref fields, u.Transforms, commitTime);
        Bump(doc, fields, commitTime);
        return new WriteResult(commitTime, transformResults);
    }

    private static WriteResult ApplyDelete(string path, DeleteWrite d,
        Dictionary<string, DocState> state, IReadOnlyDictionary<string, IReadOnlyList<string>> cascadeVictims,
        DateTimeOffset commitTime)
    {
        if (d.Cascade)
        {
            foreach (var victim in cascadeVictims[path])
                state[victim].Exists = false;
        }
        else if (state.TryGetValue(path, out var doc))
        {
            doc.Exists = false;                                  // missing → no-op (Firestore delete)
        }
        return new WriteResult(commitTime);
    }

    private static IReadOnlyDictionary<FieldPath, Value>? ApplyTransforms(
        ref IReadOnlyDictionary<string, Value> fields,
        IReadOnlyList<FieldTransform>? transforms, DateTimeOffset commitTime)
    {
        if (transforms is null || transforms.Count == 0) return null;

        var results = new Dictionary<FieldPath, Value>();
        foreach (var t in transforms)
        {
            var existing = FilterEvaluator.ResolveField(t.Field, fields);
            var transformed = TransformApplier.Apply(existing, t, commitTime);
            fields = FieldMutator.Set(fields, t.Field, transformed);
            results[t.Field] = transformed;
        }
        return results;
    }

    private static void Bump(DocState doc, IReadOnlyDictionary<string, Value> fields, DateTimeOffset commitTime)
    {
        if (!doc.Exists) doc.CreatedAt = commitTime;
        doc.Exists = true;
        doc.Fields = fields;
        doc.Version += 1;
        doc.UpdatedAt = commitTime;
    }

    // ── Load / persist ────────────────────────────────────────────────────────

    /// <summary>
    /// Single combined locking query covering exact paths and cascade subtrees.
    /// Global ORDER BY path in the SQL guarantees all concurrent transactions acquire
    /// row locks in the same order, eliminating the deadlock scenario.
    /// </summary>
    private async Task<Dictionary<string, DocState>> LoadAllLockedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        IReadOnlyList<string> exactPaths, IReadOnlyList<string> cascadeRoots,
        CancellationToken ct)
    {
        var state = new Dictionary<string, DocState>(StringComparer.Ordinal);
        if (exactPaths.Count == 0 && cascadeRoots.Count == 0) return state;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        WriteSql.LockAll(table, exactPaths, cascadeRoots).Apply(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            state[reader.GetString(0)] = ReadDocState(reader);
        return state;
    }

    private static DocState ReadDocState(NpgsqlDataReader reader) => new()
    {
        Fields = StorageCodec.Decode(reader.GetString(3)),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(4),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(5),
        Version = reader.GetInt64(6),
        VersionBefore = reader.GetInt64(6),
        ExistedBefore = true,
        Exists = true,
    };

    private async Task PersistAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        Dictionary<string, DocState> state, DateTimeOffset commitTime, CancellationToken ct)
    {
        var deletes = new List<string>();
        var changeTypes = new List<string>();
        var changePaths = new List<string>();
        var changeCollections = new List<string>();
        var changeVersions = new List<long>();

        foreach (var (path, doc) in state)
        {
            var net = (doc.ExistedBefore, doc.Exists) switch
            {
                (false, true) => "added",
                (true, true) when doc.Version != doc.VersionBefore => "modified",
                (true, false) => "removed",
                _ => null,                                       // untouched lock-only row, or absent→absent
            };
            if (net is null) continue;

            var info = DocumentPathParser.ParsePath(path);

            if (doc.Exists)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                WriteSql.Upsert(table, path, info.Id!, info.Collection,
                    StorageCodec.Encode(doc.Fields), doc.CreatedAt, doc.UpdatedAt, doc.Version,
                    doc.VersionBefore).Apply(cmd);
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected == 0)
                    // Concurrent creator raced us: the guarded ON CONFLICT WHERE was false.
                    // The whole transaction rolls back (tx disposes without commit).
                    throw new RuntimeException(RuntimeStatus.Aborted, $"Concurrent write to '{path}'.");
            }
            else
            {
                deletes.Add(path);
            }

            changeTypes.Add(net);
            changePaths.Add(path);
            changeCollections.Add(info.Collection);
            changeVersions.Add(doc.Exists ? doc.Version : doc.VersionBefore);
        }

        if (deletes.Count > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            WriteSql.DeletePaths(table, deletes).Apply(cmd);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (changePaths.Count > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            WriteSql.InsertChanges(table, changeTypes, changePaths, changeCollections, changeVersions, commitTime).Apply(cmd);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<DateTimeOffset> GetCommitTimeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT transaction_timestamp()";
        var raw = await cmd.ExecuteScalarAsync(ct);
        DateTimeOffset dto = raw switch
        {
            DateTimeOffset d => d,
            DateTime dt => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero),
            _ => throw new InvalidOperationException($"Unexpected type from transaction_timestamp(): {raw?.GetType()}"),
        };
        return Truncate(dto);
    }

    private static DateTimeOffset Truncate(DateTimeOffset t) =>
        new(t.Ticks - t.Ticks % 10, t.Offset);                   // µs (1 tick = 100ns)
}
