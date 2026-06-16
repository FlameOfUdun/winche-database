// src/Winche.Database/Querying/Sql/SchemaResolver.cs
using System.Text;
using Winche.Database.Documents;

namespace Winche.Database.Querying.Sql;

internal enum ColumnShape
{
    TaggedValue,   // jsonb tagged value ({"integerValue":"5"}, …) or SQL NULL (missing)
}

/// <summary>Document rows: metadata + tagged `data`, plus extra tagged columns.</summary>
internal sealed record DocumentSchema(IReadOnlyDictionary<string, ColumnShape> Extra)
{
    public static readonly DocumentSchema Plain = new(new Dictionary<string, ColumnShape>());
}

internal abstract record FieldRef;
/// <summary>SQL expression yielding a tagged value jsonb (or SQL NULL = missing field).</summary>
internal sealed record TaggedRef(string Sql) : FieldRef;
/// <summary>The text `path` column (__name__).</summary>
internal sealed record PathRef(string Sql) : FieldRef;

/// <summary>
/// Resolves a FieldPath against the current document schema. Column names come from
/// validated identifiers so quoting them is safe; data-path segments remain parameterized.
/// </summary>
internal sealed class SchemaResolver(DocumentSchema schema, string alias)
{
    public string Alias => alias;

    public FieldRef Resolve(FieldPath path, ParameterBag bag)
    {
        if (FieldAccessSql.IsName(path))
            return new PathRef($"{alias}.document_path");
        if (schema.Extra.ContainsKey(path.Segments[0]))
            return new TaggedRef(NestedFromColumn(path, bag));
        return new TaggedRef(FieldAccessSql.Tagged(path, bag, alias));
    }

    private string NestedFromColumn(FieldPath path, ParameterBag bag)
    {
        var sb = new StringBuilder($"{alias}.\"{path.Segments[0]}\"");
        foreach (var seg in path.Segments.Skip(1))
            sb.Append($"->'mapValue'->'fields'->{bag.Add(seg)}");
        return sb.ToString();
    }
}
