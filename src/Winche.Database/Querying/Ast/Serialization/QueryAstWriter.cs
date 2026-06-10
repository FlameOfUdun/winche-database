using System.Text.Json.Nodes;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast.Serialization;

/// <summary>Canonical Query → wire JSON. Deterministic: also the subscription grouping key source.</summary>
public static class QueryAstWriter
{
    public static JsonObject Write(Query q)
    {
        var obj = new JsonObject { ["collection"] = q.Collection };
        if (q.Where is not null) obj["where"] = WriteFilter(q.Where);
        if (q.OrderBy is { Count: > 0 })
            obj["orderBy"] = new JsonArray([.. q.OrderBy.Select(o => (JsonNode)new JsonObject
            {
                ["field"] = o.Field.ToString(),
                ["direction"] = o.Direction == SortDirection.Desc ? "desc" : "asc",
            })]);
        if (q.Limit is not null) obj["limit"] = q.Limit;
        if (q.Start is not null) obj["start"] = WriteCursor(q.Start);
        if (q.End is not null) obj["end"] = WriteCursor(q.End);
        if (q.Select is { Count: > 0 })
            obj["select"] = new JsonArray([.. q.Select.Select(f => (JsonNode)f.ToString())]);
        return obj;
    }

    public static JsonObject WriteFilter(Filter f) => f switch
    {
        CompositeFilter { Op: CompositeOp.Not } n => new JsonObject { ["not"] = WriteFilter(n.Filters[0]) },
        CompositeFilter c => new JsonObject
        {
            [c.Op == CompositeOp.And ? "and" : "or"] = new JsonArray([.. c.Filters.Select(x => (JsonNode)WriteFilter(x))]),
        },
        UnaryFilter u => new JsonObject
        {
            ["unary"] = u.Op switch { UnaryOp.IsNull => "isNull", UnaryOp.IsNan => "isNan", _ => "exists" },
            ["field"] = u.Field.ToString(),
        },
        FieldCompare cmp => new JsonObject
        {
            ["compare"] = new JsonObject
            {
                ["left"] = cmp.Left.ToString(), ["op"] = OpName(cmp.Op), ["right"] = cmp.Right.ToString(),
            },
        },
        FieldFilter ff => new JsonObject
        {
            ["field"] = ff.Field.ToString(), ["op"] = OpName(ff.Op), ["value"] = ValueSerializer.Write(ff.Operand),
        },
        _ => throw new NotSupportedException($"Unknown filter: {f.GetType().Name}"),
    };

    private static JsonObject WriteCursor(Cursor c) => new()
    {
        ["values"] = new JsonArray([.. c.Values.Select(v => (JsonNode)ValueSerializer.Write(v))]),
        ["before"] = c.Before,
    };

    internal static string OpName(FilterOperator op) => op switch
    {
        FilterOperator.Eq => "eq", FilterOperator.Ne => "ne",
        FilterOperator.Gt => "gt", FilterOperator.Gte => "gte",
        FilterOperator.Lt => "lt", FilterOperator.Lte => "lte",
        FilterOperator.In => "in", FilterOperator.NotIn => "notIn",
        FilterOperator.ArrayContains => "arrayContains",
        FilterOperator.ArrayContainsAny => "arrayContainsAny",
        FilterOperator.ArrayContainsAll => "arrayContainsAll",
        FilterOperator.Contains => "contains", FilterOperator.StartsWith => "startsWith",
        FilterOperator.EndsWith => "endsWith", FilterOperator.Regex => "regex",
        _ => throw new NotSupportedException($"Unknown op: {op}"),
    };
}
