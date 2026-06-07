// src/Winche.Database/Querying/Sql/FieldAccessSql.cs
using System.Text;
using Winche.Database.Documents;

namespace Winche.Database.Querying.Sql;

/// <summary>FieldPath → SQL accessor for the tagged value jsonb. Segments are PARAMETERIZED (injection-proof).</summary>
internal static class FieldAccessSql
{
    internal static bool IsName(FieldPath p) =>
        p.Segments.Count == 1 && p.Segments[0] == "__name__";

    /// <summary>e.g. a.b → d.data->$1->'mapValue'->'fields'->$2 (yields the tagged value object or SQL NULL).</summary>
    internal static string Tagged(FieldPath path, ParameterBag bag, string alias = "d")
    {
        var sb = new StringBuilder($"{alias}.data");
        for (var i = 0; i < path.Segments.Count; i++)
        {
            if (i > 0) sb.Append("->'mapValue'->'fields'");
            sb.Append($"->{bag.Add(path.Segments[i])}");
        }
        return sb.ToString();
    }
}
