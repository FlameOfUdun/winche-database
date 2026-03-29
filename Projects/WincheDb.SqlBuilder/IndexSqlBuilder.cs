using System.Security.Cryptography;
using System.Text;
using WincheDb.Core.Ast;
using WincheDb.SqlBuilder.Infrastructure;
using WincheDb.SqlBuilder.QuerySqlBuilders;

namespace WincheDb.SqlBuilder;

public sealed class IndexDefinition
{
    public required string Collection { get; init; }
    public required List<SortNode> Fields { get; init; }
    public string? Name { get; init; }
    public WhereNode? Where { get; init; }
}

public static class IndexSqlBuilder
{
    public static SqlBuildResult BuildCreate(IndexDefinition index, string schema, string table)
    {
        var pb = new ParameterBag();
        var sb = new StringBuilder("CREATE INDEX IF NOT EXISTS");

        var name = index.Name ?? GenerateName(index);
        sb.Append($" {name} ON {schema}.{table} ");

        var columns = OrderBySqlBuilder.Build(index.Fields);
        sb.Append($"({columns})");

        if (index.Where != null)
        {
            var where = new FilterSqlBuilder("d", pb).Build(index.Where);
            if (!string.IsNullOrEmpty(where))
            {
                sb.Append($" WHERE {where}");
            }
        }
        
        return new SqlBuildResult(sb.ToString(), pb.ToArray());
    }

    public static string BuildDrop(string schema, string name)
        => $"DROP INDEX IF EXISTS {schema}.{name}";

    private static string GenerateName(IndexDefinition index)
    {
        var parts = new List<string> { "idx", Slugify(index.Collection) };

        foreach (var f in index.Fields)
        {
            parts.Add(Slugify(f.Field));
            if (f.Direction == SortDirection.Desc) parts.Add("desc");
        }

        var name = string.Join("_", parts);
        return name.Length > 63 ? Shorten(name) : name;
    }

    private static string Shorten(string name)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(name))
        )[..8].ToLowerInvariant();

        var prefix = name[..54];
        return $"{prefix}_{hash}";
    }

    private static string Slugify(string s)
        => s.Replace("/", "_")
            .Replace(".", "_")
            .Replace("-", "_")
            .ToLowerInvariant();
}