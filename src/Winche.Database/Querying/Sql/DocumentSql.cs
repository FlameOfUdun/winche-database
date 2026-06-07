using Winche.Database.Constants;

namespace Winche.Database.Querying.Sql;

/// <summary>Single-document SQL — simple enough to need no IR (spec §6).</summary>
public static class DocumentSql
{
    private const string Columns = "path, id, collection, data, created_at, updated_at, version";

    public static CompiledSql Get(string path, bool forUpdate = false)
    {
        var bag = new ParameterBag();
        var p = bag.Add(path);
        var suffix = forUpdate ? " FOR UPDATE" : "";
        return new CompiledSql($"SELECT {Columns} FROM {WincheTables.Documents} WHERE path = {p}{suffix}", bag.ToArray());
    }

    public static CompiledSql Upsert(string path, string id, string collection, string dataJson)
    {
        var bag = new ParameterBag();
        var pPath = bag.Add(path);
        var pId = bag.Add(id);
        var pCol = bag.Add(collection);
        var pData = bag.AddJsonb(dataJson);

        var sql = $"""
            INSERT INTO {WincheTables.Documents} (path, id, collection, data, created_at, updated_at, version)
            VALUES ({pPath}, {pId}, {pCol}, {pData}, NOW(), NOW(), 1)
            ON CONFLICT (path) DO UPDATE SET data = EXCLUDED.data, updated_at = NOW(), version = {WincheTables.Documents}.version + 1
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
            UPDATE {WincheTables.Documents}
            SET data = {pData}, updated_at = NOW(), version = version + 1
            WHERE path = {pPath}
            RETURNING {Columns}
            """;
        return new CompiledSql(sql, bag.ToArray());
    }

    public static CompiledSql SelectSubtreeForUpdate(string path)
    {
        var bag = new ParameterBag();
        var pPath = bag.Add(path);
        var pPrefix = bag.Add(LikePatternEscaper.Escape(path) + "/%");
        var sql = $"""
            SELECT path FROM {WincheTables.Documents}
            WHERE path = {pPath} OR path LIKE {pPrefix} ESCAPE '\'
            FOR UPDATE
            """;
        return new CompiledSql(sql, bag.ToArray());
    }

    public static CompiledSql GetMany(IReadOnlyList<string> paths)
    {
        var bag = new ParameterBag();
        return new CompiledSql($"SELECT {Columns} FROM {WincheTables.Documents} WHERE path = ANY({bag.AddTextArray(paths.ToArray())})", bag.ToArray());
    }

    public static CompiledSql DeleteSubtree(string path)
    {
        var bag = new ParameterBag();
        var pPath = bag.Add(path);
        var pPrefix = bag.Add(LikePatternEscaper.Escape(path) + "/%");

        var sql = $"""
            DELETE FROM {WincheTables.Documents}
            WHERE path = {pPath} OR path LIKE {pPrefix} ESCAPE '\'
            RETURNING path
            """;
        return new CompiledSql(sql, bag.ToArray());
    }
}
