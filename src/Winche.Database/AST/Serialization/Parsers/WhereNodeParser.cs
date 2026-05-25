using System.Text.Json;
using System.Text.Json.Nodes;
using Winche.Database.AST.Models;

namespace Winche.Database.AST.Serialization.Parsers;

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

        FieldType? cast = null;
        if (obj["type"]?.GetValue<string>() is { Length: > 0 } castStr)
        {
            if (!Enum.TryParse<FieldType>(castStr, ignoreCase: true, out var parsedCast))
                throw new ArgumentException($"Unknown field type '{castStr}' on compare");
            cast = parsedCast;
        }

        return new FieldCompare(left, op, right, cast);
    }

    private static WhereNode ParseFieldFilter(string field, JsonNode? value)
    {
        // Primitive value → implicit Eq; no type context available
        if (value is null or JsonValue)
            return new FieldFilter(field, ConditionalOperator.Eq, ExtractValue(value));

        if (value is not JsonObject obj)
            return new FieldFilter(field, ConditionalOperator.Eq, ExtractValue(value));

        // Check if keys are operator names
        var firstKey = obj.FirstOrDefault().Key;
        if (firstKey is null || !OpMap.ContainsKey(firstKey))
            return new FieldFilter(field, ConditionalOperator.Eq, ExtractValue(value));

        // Extract optional type hint before iterating operators
        FieldType? type = null;
        if (obj["type"]?.GetValue<string>() is { Length: > 0 } typeStr)
        {
            if (!Enum.TryParse<FieldType>(typeStr, ignoreCase: true, out var parsedType))
                throw new ArgumentException($"Unknown field type '{typeStr}' on field '{field}'");
            type = parsedType;
        }

        var opKeys = obj.Select(kvp => kvp.Key).Where(k => k != "type").ToList();

        if (opKeys.Count > 1)
            throw new ArgumentException(
                $"Field '{field}' has multiple operators ({string.Join(", ", opKeys)}). Use '$and' or '$or' to combine operators.");

        if (opKeys.Count == 0)
            return new FieldFilter(field, ConditionalOperator.Eq, ExtractValue(obj));

        if (!OpMap.TryGetValue(opKeys[0], out var op))
            throw new ArgumentException($"Unknown operator: '{opKeys[0]}'");

        return new FieldFilter(field, op, ExtractValue(obj[opKeys[0]]), type);
    }

    private static object? ExtractValue(JsonNode? node)
    {
        if (node is null) return null;

        if (node is JsonArray ja)
            return ja.Select(ExtractValue).ToList();

        if (node is not JsonValue jv) return null;

        return jv.GetValueKind() switch
        {
            JsonValueKind.String => TryParseDateTime(jv.GetValue<string>()),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Number => ParseNumber(jv.GetValue<JsonElement>()),
            _ => null
        };
    }

    private static object TryParseDateTime(string s) =>
        DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dto)
            ? dto
            : s;

    private static object ParseNumber(JsonElement el)
    {
        if (el.TryGetInt64(out var l)) return l;
        if (el.TryGetDecimal(out var d)) return d;
        return el.GetDouble();
    }
}