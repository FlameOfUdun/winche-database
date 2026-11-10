using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Ast.Serialization;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// DDL for composite winche_* expression indexes. DDL cannot use parameters, so every
/// identifier-ish piece is strictly validated and quoted; field-path segments are
/// restricted to ^[A-Za-z0-9_\-]{1,128}$ (stricter than query paths — documented).
/// </summary>
public static partial class IndexSql
{
    [GeneratedRegex("^[A-Za-z0-9_\\-]{1,128}$")]
    private static partial Regex Segment();

    [GeneratedRegex("^[A-Za-z0-9_]{1,63}$")]
    private static partial Regex Identifier();

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NonIdentifierChar();

    private static string StableHash8(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    /// <summary>The index's name: <see cref="IndexDefinition.Name"/>, or one derived (stably) from the definition.</summary>
    public static string NameOf(IndexDefinition index)
    {
        if (index.Name is not null)
        {
            RequireIdentifier(index.Name);
            return index.Name;
        }

        var hashInput = $"{index.CollectionId}|{string.Join("|", index.Fields.Select(f => $"{f.Path}:{f.Direction}"))}";
        // Fold predicate into name hash so filtered and unfiltered indexes get distinct names
        if (index.Where is not null)
            hashInput += "|" + QueryAstWriter.WriteFilter(index.Where).ToJsonString();
        // Ordered keeps the historical hash input, so existing indexes keep their names.
        if (index.Kind == IndexKind.ArrayMembership)
            hashInput += "|array";
        else if (index.CollectionGroup)
            hashInput += "|group";
        var hash = StableHash8(hashInput);
        // The hash carries uniqueness/stability; the prefix is a human-readable label.
        // Collapse any non-identifier char (e.g. '-' in collection ids or field paths) to '_'
        // so the name stays DDL-safe.
        var label = $"idx_{WincheDatabaseTables.Documents}_{index.CollectionId}_{string.Join("_", index.Fields.Select(f => f.Path))}";
        var prefix = NonIdentifierChar().Replace(label, "_");
        var name = $"{prefix[..Math.Min(prefix.Length, 54)]}_{hash}";
        RequireIdentifier(name);
        return name;
    }

    /// <summary>
    /// CREATE INDEX for <paramref name="index"/>. With <paramref name="concurrently"/> it doesn't block writes
    /// while it builds (it must then run alone, outside a transaction).
    /// </summary>
    public static string BuildCreate(IndexDefinition index, bool concurrently = false)
    {
        if (index.Fields.Count == 0)
            throw new ArgumentException("Index must have at least one field.", nameof(index));
        if (index.Kind == IndexKind.ArrayMembership && index.Fields.Count != 1)
            throw new ArgumentException("An ArrayMembership index takes exactly one field.", nameof(index));

        if (!DocumentPathParser.IsValidCollectionId(index.CollectionId, out var idError))
            throw new InvalidPathPatternException(index.CollectionId, idError!);

        var name = NameOf(index);

        string columns;
        if (index.Kind == IndexKind.ArrayMembership)
        {
            columns = $"USING GIN ((winche_array_keys({LiteralAccessor(index.Fields[0].Path)})))";
        }
        else
        {
            // A collection index leads with collection_path so Postgres can seek to one collection within the
            // partial index (already narrowed to collection_id); a collection group index doesn't, so it orders
            // every collection with the id together.
            var exprs = index.CollectionGroup ? new List<string>() : new List<string> { "collection_path" };
            foreach (var field in index.Fields)
            {
                var accessor = LiteralAccessor(field.Path);
                var dir = field.Direction == SortDirection.Desc ? " DESC" : "";
                exprs.Add($"(winche_rank({accessor})){dir}");
                exprs.Add($"(winche_num({accessor})){dir}");
                exprs.Add($"(winche_num2({accessor})){dir}");
                exprs.Add($"(winche_text({accessor}) COLLATE \"C\"){dir}");
                exprs.Add($"(winche_bytes({accessor})){dir}");
                exprs.Add($"(winche_key({accessor})){dir}");
            }
            columns = $"({string.Join(", ", exprs)})";
        }

        var whereClause = $"WHERE collection_id = '{index.CollectionId.Replace("'", "''")}'";
        if (index.Where is not null)
            whereClause += $" AND ({IndexPredicateSql.Emit(index.Where)})";

        var create = concurrently ? "CREATE INDEX CONCURRENTLY IF NOT EXISTS" : "CREATE INDEX IF NOT EXISTS";
        return $"{create} \"{name}\" ON \"{WincheDatabaseTables.Documents}\" {columns} {whereClause}";
    }

    /// <summary>
    /// The indexes on the documents table that <see cref="BuildCreate"/> made: built from winche_* field
    /// functions and restricted to a collection id. Returns (name, valid).
    /// </summary>
    internal static string ListManaged() => $"""
        SELECT c.relname, i.indisvalid
        FROM pg_index i
        JOIN pg_class c ON c.oid = i.indexrelid
        JOIN pg_class t ON t.oid = i.indrelid
        WHERE t.relname = '{WincheDatabaseTables.Documents}'
          AND t.relnamespace = current_schema()::regnamespace
          AND (pg_get_indexdef(i.indexrelid) LIKE '%winche_rank(%' OR pg_get_indexdef(i.indexrelid) LIKE '%winche_array_keys(%')
          AND pg_get_expr(i.indpred, i.indrelid) LIKE '%collection_id = %'
        """;

    /// <summary>DROP INDEX CONCURRENTLY (doesn't block writes; must run alone, outside a transaction).</summary>
    internal static string BuildDropConcurrently(string name)
    {
        RequireIdentifier(name);
        return $"DROP INDEX CONCURRENTLY IF EXISTS \"{name}\"";
    }

    public static string BuildDrop(string name)
    {
        RequireIdentifier(name);
        return $"DROP INDEX IF EXISTS \"{name}\"";
    }

    /// <summary>
    /// Emits an unqualified jsonb accessor for a dotted field path (e.g. "a.b" → "data->'a'->'mapValue'->'fields'->'b'").
    /// Exposed as internal so <see cref="IndexPredicateSql"/> can reuse the same accessor form.
    /// All segments must match the strict DDL-safe pattern ^[A-Za-z0-9_\-]{1,128}$.
    /// </summary>
    internal static string LiteralAccessor(string path)
    {
        var segments = path.Split('.');
        var sb = new StringBuilder("data");
        for (var i = 0; i < segments.Length; i++)
        {
            if (!Segment().IsMatch(segments[i]))
                throw new ArgumentException($"Index field segment '{segments[i]}' must match ^[A-Za-z0-9_\\-]{{1,128}}$.");
            if (i > 0) sb.Append("->'mapValue'->'fields'");
            sb.Append($"->'{segments[i]}'");
        }
        return sb.ToString();
    }

    private static void RequireIdentifier(string s)
    {
        if (!Identifier().IsMatch(s))
            throw new ArgumentException($"'{s}' is not a valid SQL identifier ([A-Za-z0-9_]{{1,63}}).");
    }
}
