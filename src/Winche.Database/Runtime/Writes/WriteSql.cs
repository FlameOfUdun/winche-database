using Winche.Database.Constants;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Runtime.Writes;

/// <summary>SQL for the single mutation path. Identifiers from config only; every value is a parameter.</summary>
internal static class WriteSql
{
    private const string Columns = "document_path, document_id, collection_path, collection_id, data, created_at, updated_at, version";

    /// <summary>
    /// Transaction-scoped advisory lock over every path in <paramref name="exactPaths"/>, acquired in
    /// array order (the caller passes them pre-sorted, so every writer takes them in the same order —
    /// no deadlock among these locks, and none against the row locks taken afterward since these are
    /// always acquired first). FOR UPDATE only locks rows that exist, so on its own it leaves a path
    /// with no row unprotected against a concurrent creator; hashing the path itself into
    /// pg_advisory_xact_lock closes that gap without needing a row to hang the lock on. Released
    /// automatically at commit/rollback.
    /// </summary>
    internal static CompiledSql AdvisoryLockAll(IReadOnlyList<string> exactPaths)
    {
        var bag = new ParameterBag();
        var sql = $"SELECT pg_advisory_xact_lock(hashtextextended(p, 0)) " +
                  $"FROM unnest({bag.Add(exactPaths.ToArray())}::text[]) WITH ORDINALITY AS t(p, ord)";
        return new CompiledSql(sql, bag.ToArray());
    }

    /// <summary>
    /// Locks exact paths and cascade-subtree paths in a SINGLE statement with a global ORDER BY document_path,
    /// preventing deadlocks across concurrent batches that share overlapping lock sets.
    /// </summary>
    internal static CompiledSql LockAll(IReadOnlyList<string> exactPaths, IReadOnlyList<string> cascadeRoots)
    {
        var bag = new ParameterBag();
        var clauses = new List<string>();
        if (exactPaths.Count > 0)
            clauses.Add($"document_path = ANY({bag.Add(exactPaths.ToArray())})");
        foreach (var root in cascadeRoots)
            clauses.Add($"document_path = {bag.Add(root)} OR document_path LIKE {bag.Add(LikePatternEscaper.Escape(root) + "/%")} ESCAPE '\\'");
        var sql = $"SELECT {Columns} FROM {WincheDatabaseTables.Documents} WHERE {string.Join(" OR ", clauses)} ORDER BY document_path FOR UPDATE";
        return new CompiledSql(sql, bag.ToArray());
    }

    /// <summary>
    /// Guarded upsert: the conflict clause includes a WHERE predicate on the current version so that
    /// a concurrent creator (which also computed versionBefore=0) causes the affected-rows count to
    /// return 0 (the WHERE is false), allowing the caller to detect and abort the transaction.
    /// For existing rows held under FOR UPDATE the guard always passes (no behavior change).
    /// </summary>
    internal static CompiledSql Upsert(string path, string id, string collection, string collectionId,
        string dataJson, DateTimeOffset createdAt, DateTimeOffset updatedAt, long version, long versionBefore)
    {
        var bag = new ParameterBag();
        var sql = $"""
            INSERT INTO {WincheDatabaseTables.Documents} (document_path, document_id, collection_path, collection_id, data, created_at, updated_at, version)
            VALUES ({bag.Add(path)}, {bag.Add(id)}, {bag.Add(collection)}, {bag.Add(collectionId)}, {bag.AddJsonb(dataJson)},
                    {bag.Add(createdAt)}, {bag.Add(updatedAt)}, {bag.Add(version)})
            ON CONFLICT (document_path) DO UPDATE
            SET data = EXCLUDED.data, updated_at = EXCLUDED.updated_at, version = EXCLUDED.version
            WHERE {WincheDatabaseTables.Documents}.version = {bag.Add(versionBefore)}
            """;
        return new CompiledSql(sql, bag.ToArray());
    }

    internal static CompiledSql DeletePaths(IReadOnlyList<string> paths)
    {
        var bag = new ParameterBag();
        return new CompiledSql($"DELETE FROM {WincheDatabaseTables.Documents} WHERE document_path = ANY({bag.Add(paths.ToArray())})", bag.ToArray());
    }

    internal static CompiledSql InsertChanges(
        IReadOnlyList<string> types, IReadOnlyList<string> paths, IReadOnlyList<string> collections,
        IReadOnlyList<long> versions, DateTimeOffset commitTime)
    {
        var bag = new ParameterBag();
        var sql = $"""
            INSERT INTO {WincheDatabaseTables.Changes} (type, document_path, collection_path, version, commit_time)
            SELECT t, p, c, v, {bag.Add(commitTime)}
            FROM unnest({bag.Add(types.ToArray())}::text[], {bag.Add(paths.ToArray())}::text[],
                        {bag.Add(collections.ToArray())}::text[], {bag.Add(versions.ToArray())}::bigint[]) AS u(t, p, c, v)
            """;
        return new CompiledSql(sql, bag.ToArray());
    }
}
