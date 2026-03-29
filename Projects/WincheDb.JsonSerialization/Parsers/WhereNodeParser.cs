using System.Text.Json;
using System.Text.Json.Nodes;
using WincheDb.Core.Ast;

namespace WincheDb.JsonSerialization.Parsers;

internal static class WhereNodeParser
{
    private static readonly Dictionary<string, ConditionalOperator> OpMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["$eq"] = ConditionalOperator.Eq,
            ["$ne"] = ConditionalOperator.Ne,
            ["$gt"] = ConditionalOperator.Gt,
            ["$gte"] = ConditionalOperator.Gte,
            ["$lt"] = ConditionalOperator.Lt,
            ["$lte"] = ConditionalOperator.Lte,
            ["$in"] = ConditionalOperator.In,
            ["$nin"] = ConditionalOperator.Nin,
            ["$contains"] = ConditionalOperator.Contains,
            ["$startsWith"] = ConditionalOperator.StartsWith,
            ["$endsWith"] = ConditionalOperator.EndsWith,
            ["$regex"] = ConditionalOperator.Regex,
            ["$arrContains"] = ConditionalOperator.ArrContains,
            ["$arrContainsAny"] = ConditionalOperator.ArrContainsAny,
            ["$arrContainsAll"] = ConditionalOperator.ArrContainsAll,
            ["$exists"] = ConditionalOperator.Exists,
        };

    public static WhereNode? Parse(JsonObject? obj)
    {
        if (obj is null || obj.Count == 0) return null;

        var nodes = new List<WhereNode>();

        foreach (var (key, value) in obj)
        {
            var node = key switch
            {
                "$and" => ParseLogic(LogicalOperator.And, value),
                "$or" => ParseLogic(LogicalOperator.Or, value),
                "$not" => ParseNot(value),
                "$compare" => ParseFieldCompare(value),
                _ => ParseFieldFilter(key, value)
            };

            if (node is not null) nodes.Add(node);
        }

        return nodes.Count switch
        {
            0 => null,
            1 => nodes[0],
            _ => new LogicGroup(LogicalOperator.And, nodes)
        };
    }

    private static LogicGroup ParseLogic(LogicalOperator op, JsonNode? value)
    {
        if (value is not JsonArray arr)
            throw new ArgumentException($"'{op.ToString().ToLower()}' requires an array");

        var children = arr
            .OfType<JsonObject>()
            .Select(Parse)
            .Where(n => n is not null)
            .Cast<WhereNode>()
            .ToList();

        return new LogicGroup(op, children);
    }

    private static WhereNode ParseNot(JsonNode? value)
    {
        if (value is not JsonObject obj)
            throw new ArgumentException("'not' requires an object");

        var inner = Parse(obj)
            ?? throw new ArgumentException("'not' filter resolved to empty");

        return new LogicGroup(LogicalOperator.Not, [inner]);
    }


    private static FieldCompare ParseFieldCompare(JsonNode? value)
    {
        if (value is not JsonObject obj)
            throw new ArgumentException("'compare' requires an object");

        var left = obj["left"]?.GetValue<string>()
            ?? throw new ArgumentException("'compare' requires 'left'");
        var right = obj["right"]?.GetValue<string>()
            ?? throw new ArgumentException("'compare' requires 'right'");
        var opStr = obj["operator"]?.GetValue<string>()
            ?? throw new ArgumentException("'compare' requires 'operator'");

        if (!OpMap.TryGetValue(opStr, out var op))
            throw new ArgumentException($"Unknown compare operator: '{opStr}'");

        var cast = obj["type"]?.GetValue<string>() is { Length: > 0 } c
            ? Enum.Parse<FieldType>(c, ignoreCase: true)
            : (FieldType?)null;

        return new FieldCompare(left, op, right, cast);
    }

    private static WhereNode ParseFieldFilter(string field, JsonNode? value)
    {
        // Primitive value → implicit Eq
        if (value is null or JsonValue)
            return new FieldFilter(field, ConditionalOperator.Eq, ExtractValue(value));

        if (value is not JsonObject obj)
            return new FieldFilter(field, ConditionalOperator.Eq, ExtractValue(value));

        // Check if keys are operator names
        var firstKey = obj.FirstOrDefault().Key;
        if (firstKey is null || !OpMap.ContainsKey(firstKey))
            return new FieldFilter(field, ConditionalOperator.Eq, ExtractValue(value));

        // One FieldFilter per operator key
        var filters = new List<WhereNode>();
        foreach (var (opKey, opValue) in obj)
        {
            if (!OpMap.TryGetValue(opKey, out var op))
                throw new ArgumentException($"Unknown operator: '{opKey}'");

            filters.Add(new FieldFilter(field, op, ExtractValue(opValue)));
        }

        return filters.Count == 1
            ? filters[0]
            : new LogicGroup(LogicalOperator.And, filters);
    }

    private static object? ExtractValue(JsonNode? node)
    {
        if (node is null) return null;

        if (node is JsonArray ja)
            return ja.Select(ExtractValue).ToList();

        if (node is not JsonValue jv) return null;

        return jv.GetValueKind() switch
        {
            JsonValueKind.String => jv.GetValue<string>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Number => ParseNumber(jv.GetValue<JsonElement>()),
            _ => null
        };
    }

    private static object ParseNumber(JsonElement el)
    {
        if (el.TryGetInt64(out var l)) return l;
        if (el.TryGetDecimal(out var d)) return d;
        return el.GetDouble();
    }
}