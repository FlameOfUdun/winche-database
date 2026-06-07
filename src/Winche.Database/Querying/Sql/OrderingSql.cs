// src/Winche.Database/Querying/Sql/OrderingSql.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// Sort keys → ORDER BY list. Six-expression family per field implements the Firestore
/// total order (rank, then per-class payloads; winche_key last covers arrays/maps).
/// Postgres default NULL placement is consistent within a rank, so no explicit NULLS needed.
/// </summary>
internal static class OrderingSql
{
    internal static string Build(IReadOnlyList<SortKey> keys, ParameterBag bag, string alias = "d") =>
        Build(keys, bag, new SchemaResolver(DocumentSchema.Plain, alias));

    internal static string Build(IReadOnlyList<SortKey> keys, ParameterBag bag, SchemaResolver resolver)
    {
        var parts = new List<string>();
        foreach (var k in keys)
        {
            var dir = k.Direction == SortDirection.Desc ? "DESC" : "ASC";
            switch (resolver.Resolve(k.Field, bag))
            {
                case PathRef p:
                    parts.Add($"{p.Sql} COLLATE \"C\" {dir}");
                    break;
                case TaggedRef t:
                    parts.Add($"winche_rank({t.Sql}) {dir}");
                    parts.Add($"winche_num({t.Sql}) {dir}");
                    parts.Add($"winche_num2({t.Sql}) {dir}");
                    parts.Add($"winche_text({t.Sql}) COLLATE \"C\" {dir}");
                    parts.Add($"winche_bytes({t.Sql}) {dir}");
                    parts.Add($"winche_key({t.Sql}) {dir}");
                    break;
            }
        }
        return string.Join(", ", parts);
    }
}
