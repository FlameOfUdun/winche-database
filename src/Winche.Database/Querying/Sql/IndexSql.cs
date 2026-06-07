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

    private static string StableHash8(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    public static string BuildCreate(IndexDefinition index)
    {
        if (index.Fields.Count == 0)
            throw new ArgumentException("Index must have at least one field.", nameof(index));

        string name;
        if (index.Name is not null)
        {
            name = index.Name;
        }
        else
        {
            var hashInput = $"{index.Collection}|{string.Join("|", index.Fields.Select(f => $"{f.Path}:{f.Direction}"))}";
            // Fold predicate into name hash so filtered and unfiltered indexes get distinct names
            if (index.Where is not null)
                hashInput += "|" + QueryAstWriter.WriteFilter(index.Where).ToJsonString();
            var hash = StableHash8(hashInput);
            var prefix = $"idx_{WincheTables.Documents}_{index.Collection}_{string.Join("_", index.Fields.Select(f => f.Path.Replace('.', '_').Replace('-', '_')))}";
            name = $"{prefix[..Math.Min(prefix.Length, 54)]}_{hash}";
        }

        RequireIdentifier(name);

        var exprs = new List<string>();
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

        var collection = index.Collection.Replace("'", "''");
        var whereClause = $"WHERE collection = '{collection}'";
        if (index.Where is not null)
            whereClause += $" AND ({IndexPredicateSql.Emit(index.Where)})";
        return $"CREATE INDEX IF NOT EXISTS \"{name}\" ON \"{WincheTables.Documents}\" ({string.Join(", ", exprs)}) {whereClause}";
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
