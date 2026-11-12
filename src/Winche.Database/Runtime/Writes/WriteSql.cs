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
            clauses.Add(DocumentSql.SubtreeWhere(bag, root));
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
        IReadOnlyList<long> versions, IReadOnlyList<string?> before, IReadOnlyList<string?> after, DateTimeOffset commitTime,
        string? actor = null)
    {
        var bag = new ParameterBag();
        var sql = $"""
            INSERT INTO {WincheDatabaseTables.Changes} (type, document_path, collection_path, version, commit_time, before, after, actor)
            SELECT t, p, c, v, {bag.Add(commitTime)}, b, a, {bag.Add((object?)actor ?? DBNull.Value)}::text
            FROM unnest({bag.Add(types.ToArray())}::text[], {bag.Add(paths.ToArray())}::text[],
                        {bag.Add(collections.ToArray())}::text[], {bag.Add(versions.ToArray())}::bigint[],
                        {bag.AddJsonbArray([.. before])}, {bag.AddJsonbArray([.. after])}) AS u(t, p, c, v, b, a)
            """;
        return new CompiledSql(sql, bag.ToArray());
    }
}
