using Winche.Database.Constants;

namespace Winche.Database.Querying.Sql;

/// <summary>Single-document SQL — simple enough to need no IR (spec §6).</summary>
public static class DocumentSql
{
    private const string Columns = "document_path, document_id, collection_path, collection_id, data, created_at, updated_at, version";

    public static CompiledSql Get(string path, bool forUpdate = false)
    {
        var bag = new ParameterBag();
        var p = bag.Add(path);
        var suffix = forUpdate ? " FOR UPDATE" : "";
        return new CompiledSql($"SELECT {Columns} FROM {WincheDatabaseTables.Documents} WHERE document_path = {p}{suffix}", bag.ToArray());
    }

    public static CompiledSql Upsert(string path, string id, string collection, string collectionId, string dataJson)
    {
        var bag = new ParameterBag();
        var pPath = bag.Add(path);
        var pId = bag.Add(id);
        var pCol = bag.Add(collection);
        var pColId = bag.Add(collectionId);
        var pData = bag.AddJsonb(dataJson);

        var sql = $"""
            INSERT INTO {WincheDatabaseTables.Documents} (document_path, document_id, collection_path, collection_id, data, created_at, updated_at, version)
            VALUES ({pPath}, {pId}, {pCol}, {pColId}, {pData}, NOW(), NOW(), 1)
            ON CONFLICT (document_path) DO UPDATE SET data = EXCLUDED.data, updated_at = NOW(), version = {WincheDatabaseTables.Documents}.version + 1
            RETURNING {Columns}
            """;
        return new CompiledSql(sql, bag.ToArray());
    }

    public static CompiledSql UpdateData(string path, string dataJson)
    {
        var bag = new ParameterBag();
        var pPath = bag.Add(path);
        var pData = bag.AddJsonb(dataJson);

        var sql = $"""
            UPDATE {WincheDatabaseTables.Documents}
            SET data = {pData}, updated_at = NOW(), version = version + 1
            WHERE document_path = {pPath}
            RETURNING {Columns}
            """;
        return new CompiledSql(sql, bag.ToArray());
    }

    /// <summary>
    /// <paramref name="root"/> and every document beneath it, as a byte-order range on the path:
    /// <c>root/ &lt;= path &lt; root0</c> ('0' follows '/'), under COLLATE "C" so the
    /// <c>idx_winche_documents_path_c</c> index serves it — a LIKE prefix can't use an index under a
    /// non-C database collation, and with a parameter not even under C once the plan is cached.
    /// </summary>
    internal static string SubtreeWhere(ParameterBag bag, string root) =>
        $"(document_path = {bag.Add(root)} OR (document_path >= {bag.Add(root + "/")} COLLATE \"C\" AND document_path < {bag.Add(root + "0")} COLLATE \"C\"))";

    public static CompiledSql SelectSubtreeForUpdate(string path)
    {
        var bag = new ParameterBag();
        var sql = $"""
            SELECT document_path FROM {WincheDatabaseTables.Documents}
            WHERE {SubtreeWhere(bag, path)}
            FOR UPDATE
            """;
        return new CompiledSql(sql, bag.ToArray());
    }

    public static CompiledSql GetMany(IReadOnlyList<string> paths)
    {
        var bag = new ParameterBag();
        return new CompiledSql($"SELECT {Columns} FROM {WincheDatabaseTables.Documents} WHERE document_path = ANY({bag.AddTextArray(paths.ToArray())})", bag.ToArray());
    }

    public static CompiledSql DeleteSubtree(string path)
    {
        var bag = new ParameterBag();
        var sql = $"""
            DELETE FROM {WincheDatabaseTables.Documents}
            WHERE {SubtreeWhere(bag, path)}
            RETURNING document_path
            """;
        return new CompiledSql(sql, bag.ToArray());
    }
}
