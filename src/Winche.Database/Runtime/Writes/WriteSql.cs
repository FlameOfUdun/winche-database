using Winche.Database.Constants;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Runtime.Writes;

/// <summary>SQL for the single mutation path. Identifiers from config only; every value is a parameter.</summary>
internal static class WriteSql
{
    private const string Columns = "document_path, document_id, collection_path, collection_id, data, created_at, updated_at, version";

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
        var sql = $"SELECT {Columns} FROM {WincheTables.Documents} WHERE {string.Join(" OR ", clauses)} ORDER BY document_path FOR UPDATE";
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
            INSERT INTO {WincheTables.Documents} (document_path, document_id, collection_path, collection_id, data, created_at, updated_at, version)
            VALUES ({bag.Add(path)}, {bag.Add(id)}, {bag.Add(collection)}, {bag.Add(collectionId)}, {bag.AddJsonb(dataJson)},
                    {bag.Add(createdAt)}, {bag.Add(updatedAt)}, {bag.Add(version)})
            ON CONFLICT (document_path) DO UPDATE
            SET data = EXCLUDED.data, updated_at = EXCLUDED.updated_at, version = EXCLUDED.version
            WHERE {WincheTables.Documents}.version = {bag.Add(versionBefore)}
            """;
        return new CompiledSql(sql, bag.ToArray());
    }

    internal static CompiledSql DeletePaths(IReadOnlyList<string> paths)
    {
        var bag = new ParameterBag();
        return new CompiledSql($"DELETE FROM {WincheTables.Documents} WHERE document_path = ANY({bag.Add(paths.ToArray())})", bag.ToArray());
    }

    internal static CompiledSql InsertChanges(
        IReadOnlyList<string> types, IReadOnlyList<string> paths, IReadOnlyList<string> collections,
        IReadOnlyList<long> versions, DateTimeOffset commitTime)
    {
        var bag = new ParameterBag();
        var sql = $"""
            INSERT INTO {WincheTables.Changes} (type, document_path, collection_path, version, commit_time)
            SELECT t, p, c, v, {bag.Add(commitTime)}
            FROM unnest({bag.Add(types.ToArray())}::text[], {bag.Add(paths.ToArray())}::text[],
                        {bag.Add(collections.ToArray())}::text[], {bag.Add(versions.ToArray())}::bigint[]) AS u(t, p, c, v)
            """;
        return new CompiledSql(sql, bag.ToArray());
    }
}
