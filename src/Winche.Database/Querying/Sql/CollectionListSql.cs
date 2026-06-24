using Winche.Database.Constants;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// Builds the descendant-scan SQL for ListCollectionIds.
///
/// A subcollection X exists under document P iff some document exists at "P/X/..."
/// (even when the intermediate document "P/X/&lt;id&gt;" is missing). So we scan every
/// descendant of P and take the path segment immediately after P.
///
/// COLLATE "C" is applied to all comparisons/ordering so results follow
/// UTF-8 byte ordering and keyset pagination stays self-consistent.
/// </summary>
public static class CollectionListSql
{
    /// <param name="parentDocumentPath">Parent document path; null/empty = database root.</param>
    /// <param name="after">Exclusive keyset cursor (last collection id of the previous page); null = first page.</param>
    /// <param name="limit">Row limit to emit (the caller passes pageSize + 1 to detect more pages).</param>
    public static CompiledSql Build(string? parentDocumentPath, string? after, int limit)
    {
        var bag = new ParameterBag();
        var root = string.IsNullOrEmpty(parentDocumentPath);

        string cidExpr;
        string where;
        if (root)
        {
            // First path segment = top-level collection id.
            cidExpr = "split_part(document_path, '/', 1)";
            where = "TRUE";
        }
        else
        {
            // lo = "P/"; hi = smallest string greater than every "P/..." descendant.
            // The lo prefix ends in '/'; incrementing that byte ('/' 0x2F -> '0' 0x30)
            // yields the exclusive upper bound. Both boundary chars are ASCII, so the
            // bound is correct under COLLATE "C" regardless of other bytes in P.
            var pLo = bag.Add(parentDocumentPath + "/");
            var pHi = bag.Add(parentDocumentPath + (char)('/' + 1));
            cidExpr = $"split_part(substr(document_path, char_length({pLo}) + 1), '/', 1)";
            where = $"document_path >= {pLo} COLLATE \"C\" AND document_path < {pHi} COLLATE \"C\"";
        }

        var pAfter = bag.Add(after);   // string or null -> DBNull
        var pLimit = bag.Add(limit);

        var sql = $"""
            SELECT cid
            FROM (
                SELECT DISTINCT {cidExpr} AS cid
                FROM {WincheTables.Documents}
                WHERE {where}
            ) sub
            WHERE ({pAfter}::text IS NULL OR cid > {pAfter}::text COLLATE "C")
            ORDER BY cid COLLATE "C"
            LIMIT {pLimit}
            """;
        return new CompiledSql(sql, bag.ToArray());
    }
}
