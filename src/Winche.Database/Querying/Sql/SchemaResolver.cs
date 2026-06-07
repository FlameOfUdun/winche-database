// src/Winche.Database/Querying/Sql/SchemaResolver.cs
using System.Text;
using Winche.Database.Documents;

namespace Winche.Database.Querying.Sql;

internal abstract record FieldRef;
/// <summary>SQL expression yielding a tagged value jsonb (or SQL NULL = missing field).</summary>
internal sealed record TaggedRef(string Sql) : FieldRef;
/// <summary>The text `path` column (__name__).</summary>
internal sealed record PathRef(string Sql) : FieldRef;

/// <summary>
/// Resolves a FieldPath against the current stage schema. Column names come from
/// validated `as` identifiers (Normalizer rule AS_NAME) so quoting them is safe;
/// data-path segments remain parameterized.
/// </summary>
internal sealed class SchemaResolver(StageSchema schema, string alias)
{
    public string Alias => alias;

    public FieldRef Resolve(FieldPath path, ParameterBag bag)
    {
        switch (schema)
        {
            case DocumentSchema doc:
                if (FieldAccessSql.IsName(path))
                    return new PathRef($"{alias}.path");
                if (doc.Extra.ContainsKey(path.Segments[0]))
                    return new TaggedRef(NestedFromColumn(path, bag));
                return new TaggedRef(FieldAccessSql.Tagged(path, bag, alias));

            case RowSchema row:
                if (!row.Columns.ContainsKey(path.Segments[0]))
                    return new TaggedRef("NULL::jsonb");
                return new TaggedRef(NestedFromColumn(path, bag));

            default:
                throw new NotSupportedException($"Unknown schema: {schema.GetType().Name}");
        }
    }

    private string NestedFromColumn(FieldPath path, ParameterBag bag)
    {
        var sb = new StringBuilder($"{alias}.\"{path.Segments[0]}\"");
        foreach (var seg in path.Segments.Skip(1))
            sb.Append($"->'mapValue'->'fields'->{bag.Add(seg)}");
        return sb.ToString();
    }
}
