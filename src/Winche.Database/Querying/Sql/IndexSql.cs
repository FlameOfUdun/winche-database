using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;

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

    public static string BuildCreate(IndexDefinition index, string schema, string table)
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
            var hash = StableHash8(hashInput);
            var prefix = $"idx_{table}_{index.Collection}_{string.Join("_", index.Fields.Select(f => f.Path.Replace('.', '_').Replace('-', '_')))}";
            name = $"{prefix[..Math.Min(prefix.Length, 54)]}_{hash}";
        }

        RequireIdentifier(schema); RequireIdentifier(table); RequireIdentifier(name);

        var exprs = new List<string>();
        foreach (var field in index.Fields)
        {
            var accessor = Accessor(field.Path);
            var dir = field.Direction == SortDirection.Desc ? " DESC" : "";
            exprs.Add($"(winche_rank({accessor})){dir}");
            exprs.Add($"(winche_num({accessor})){dir}");
            exprs.Add($"(winche_num2({accessor})){dir}");
            exprs.Add($"(winche_text({accessor}) COLLATE \"C\"){dir}");
            exprs.Add($"(winche_bytes({accessor})){dir}");
            exprs.Add($"(winche_key({accessor})){dir}");
        }

        var collection = index.Collection.Replace("'", "''");
        return $"CREATE INDEX IF NOT EXISTS \"{name}\" ON \"{schema}\".\"{table}\" ({string.Join(", ", exprs)}) WHERE collection = '{collection}'";
    }

    public static string BuildDrop(string schema, string name)
    {
        RequireIdentifier(schema); RequireIdentifier(name);
        return $"DROP INDEX IF EXISTS \"{schema}\".\"{name}\"";
    }

    private static string Accessor(string path)
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
