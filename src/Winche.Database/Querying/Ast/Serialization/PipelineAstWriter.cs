using System.Text.Json.Nodes;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast.Serialization;

/// <summary>Canonical Pipeline → wire JSON.</summary>
public static class PipelineAstWriter
{
    public static JsonObject Write(Pipeline p) =>
        new() { ["pipeline"] = new JsonArray([.. p.Stages.Select(s => (JsonNode)WriteStage(s))]) };

    private static JsonObject WriteStage(Stage s) => s switch
    {
        Match m => new JsonObject
        {
            ["match"] = new JsonObject
            {
                ["collection"] = m.Collection,
                ["where"] = m.Where is null ? null : QueryAstWriter.WriteFilter(m.Where),
            },
        },
        Where f => new JsonObject { ["filter"] = QueryAstWriter.WriteFilter(f.Predicate) },
        Lookup l => new JsonObject
        {
            ["lookup"] = new JsonObject
            {
                ["collection"] = l.Collection,
                ["localField"] = l.LocalField.ToString(),
                ["foreignField"] = l.ForeignField.ToString(),
                ["as"] = l.As,
                ["where"] = l.Where is null ? null : QueryAstWriter.WriteFilter(l.Where),
                ["orderBy"] = l.OrderBy is null ? null : new JsonArray([.. l.OrderBy.Select(o => (JsonNode)new JsonObject
                {
                    ["field"] = o.Field.ToString(),
                    ["direction"] = o.Direction == SortDirection.Desc ? "desc" : "asc",
                })]),
                ["limit"] = l.Limit,
            },
        },
        Unwind u => new JsonObject
        {
            ["unwind"] = new JsonObject
            {
                ["field"] = u.Field.ToString(), ["as"] = u.As, ["preserveNullAndEmpty"] = u.PreserveNullAndEmpty,
            },
        },
        Group g => new JsonObject
        {
            ["group"] = new JsonObject
            {
                ["keys"] = new JsonArray([.. g.Keys.Select(k => (JsonNode)new JsonObject
                    { ["as"] = k.As, ["field"] = k.Field.ToString() })]),
                ["accumulators"] = new JsonArray([.. g.Accumulators.Select(a => (JsonNode)WriteAcc(a))]),
                ["having"] = g.Having is null ? null : QueryAstWriter.WriteFilter(g.Having),
            },
        },
        Project pr => new JsonObject
        {
            ["project"] = new JsonObject
            {
                ["fields"] = new JsonArray([.. pr.Fields.Select(f => (JsonNode)WriteProjection(f))]),
            },
        },
        Sort so => new JsonObject
        {
            ["sort"] = new JsonArray([.. so.Fields.Select(o => (JsonNode)new JsonObject
            {
                ["field"] = o.Field.ToString(),
                ["direction"] = o.Direction == SortDirection.Desc ? "desc" : "asc",
            })]),
        },
        Limit li => new JsonObject { ["limit"] = li.Count },
        Skip sk => new JsonObject { ["skip"] = sk.Count },
        _ => throw new NotSupportedException($"Unknown stage: {s.GetType().Name}"),
    };

    private static JsonObject WriteAcc(Accumulator a)
    {
        var obj = new JsonObject { ["as"] = a.As, ["fn"] = FnName(a.Fn) };
        if (a.Field is not null) obj["field"] = a.Field.ToString();
        return obj;
    }

    private static JsonObject WriteProjection(Projection p) => p.Expr switch
    {
        FieldRefExpr f => new JsonObject { ["as"] = p.As, ["field"] = f.Field.ToString() },
        LiteralExpr l => new JsonObject { ["as"] = p.As, ["value"] = ValueSerializer.Write(l.Value) },
        AggFuncExpr a => a.Field is null
            ? new JsonObject { ["as"] = p.As, ["fn"] = FnName(a.Fn) }
            : new JsonObject { ["as"] = p.As, ["fn"] = FnName(a.Fn), ["field"] = a.Field.ToString() },
        _ => throw new NotSupportedException($"Unknown projection: {p.Expr.GetType().Name}"),
    };

    private static string FnName(AggFunction fn) => fn switch
    {
        AggFunction.Count => "count", AggFunction.Sum => "sum", AggFunction.Avg => "avg",
        AggFunction.Min => "min", AggFunction.Max => "max", AggFunction.Push => "push",
        AggFunction.AddToSet => "addToSet", AggFunction.First => "first", AggFunction.Last => "last",
        _ => throw new NotSupportedException($"Unknown fn: {fn}"),
    };
}
