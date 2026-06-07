using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast;

/// <summary>Wire JSON → QueryAst. Pure shape translation; semantic rules live in Planning.Normalizer.</summary>
public static class QueryParser
{
    private static readonly Dictionary<string, FilterOperator> Ops = new(StringComparer.Ordinal)
    {
        ["eq"] = FilterOperator.Eq, ["ne"] = FilterOperator.Ne,
        ["gt"] = FilterOperator.Gt, ["gte"] = FilterOperator.Gte,
        ["lt"] = FilterOperator.Lt, ["lte"] = FilterOperator.Lte,
        ["in"] = FilterOperator.In, ["notIn"] = FilterOperator.NotIn,
        ["arrayContains"] = FilterOperator.ArrayContains,
        ["arrayContainsAny"] = FilterOperator.ArrayContainsAny,
        ["arrayContainsAll"] = FilterOperator.ArrayContainsAll,
        ["contains"] = FilterOperator.Contains,
        ["startsWith"] = FilterOperator.StartsWith,
        ["endsWith"] = FilterOperator.EndsWith,
        ["regex"] = FilterOperator.Regex,
    };

    private static readonly Dictionary<string, UnaryOp> Unaries = new(StringComparer.Ordinal)
    {
        ["isNull"] = UnaryOp.IsNull, ["isNan"] = UnaryOp.IsNan, ["exists"] = UnaryOp.Exists,
    };

    public static QueryAst Parse(JsonObject json)
    {
        var collection = json["collection"] is JsonValue cv && cv.TryGetValue<string>(out var c) && c.Length > 0
            ? c : throw new QueryParseException("'collection' is required and must be a non-empty string", "$.collection");

        FilterAst? where = json["where"] is { } w ? ParseFilter(w, "$.where") : null;

        IReadOnlyList<OrderAst>? orderBy = null;
        if (json["orderBy"] is { } ob)
        {
            if (ob is not JsonArray arr)
                throw new QueryParseException("'orderBy' must be an array", "$.orderBy");
            orderBy = [.. arr.Select((n, i) => ParseOrder(n, $"$.orderBy[{i}]"))];
        }

        int? limit = null;
        if (json["limit"] is { } ln)
        {
            if (ln is not JsonValue lv || !lv.TryGetValue<int>(out var l))
                throw new QueryParseException("'limit' must be an integer", "$.limit");
            limit = l;
        }

        var start = json["start"] is { } s ? ParseCursor(s, "$.start") : null;
        var end = json["end"] is { } e ? ParseCursor(e, "$.end") : null;

        return new QueryAst(collection, where, orderBy, limit, start, end);
    }

    public static FilterAst ParseFilter(JsonNode node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("Filter must be an object", path);

        var shapeKeys = new[] { "and", "or", "not", "unary", "compare" }.Count(obj.ContainsKey)
                      + (obj.ContainsKey("field") && obj.ContainsKey("op") && !obj.ContainsKey("unary") ? 1 : 0);
        if (shapeKeys > 1)
            throw new QueryParseException("Filter must be exactly one of: and/or/not, field+op+value, unary+field, compare", path);

        if (obj.ContainsKey("and")) return ParseComposite(CompositeOp.And, obj["and"], $"{path}.and");
        if (obj.ContainsKey("or")) return ParseComposite(CompositeOp.Or, obj["or"], $"{path}.or");

        if (obj.ContainsKey("not"))
        {
            if (obj["not"] is not JsonObject inner)
                throw new QueryParseException("'not' must be a filter object", $"{path}.not");
            return new CompositeFilterAst(CompositeOp.Not, [ParseFilter(inner, $"{path}.not")]);
        }

        if (obj.ContainsKey("unary"))
        {
            var op = obj["unary"] is JsonValue uv && uv.TryGetValue<string>(out var u) && Unaries.TryGetValue(u, out var parsed)
                ? parsed : throw new QueryParseException("'unary' must be one of isNull|isNan|exists", $"{path}.unary");
            return new UnaryFilterAst(ParseFieldPath(obj["field"], $"{path}.field"), op);
        }

        if (obj.ContainsKey("compare"))
        {
            if (obj["compare"] is not JsonObject cmp)
                throw new QueryParseException("'compare' must be an object", $"{path}.compare");
            return new FieldCompareAst(
                ParseFieldPath(cmp["left"], $"{path}.compare.left"),
                ParseOp(cmp["op"], $"{path}.compare.op"),
                ParseFieldPath(cmp["right"], $"{path}.compare.right"));
        }

        if (obj.ContainsKey("field") && obj.ContainsKey("op"))
        {
            var field = ParseFieldPath(obj["field"], $"{path}.field");
            var op = ParseOp(obj["op"], $"{path}.op");
            var valueNode = obj["value"]
                ?? throw new QueryParseException("'value' is required for a field filter", $"{path}.value");
            return new FieldFilterAst(field, op, ParseValue(valueNode, $"{path}.value"));
        }

        throw new QueryParseException(
            "Filter must be one of: and/or/not, field+op+value, unary+field, compare", path);
    }

    private static CompositeFilterAst ParseComposite(CompositeOp op, JsonNode? node, string path)
    {
        if (node is not JsonArray arr)
            throw new QueryParseException($"'{op.ToString().ToLowerInvariant()}' must be an array", path);
        return new CompositeFilterAst(op, [.. arr.Select((n, i) =>
            ParseFilter(n ?? throw new QueryParseException("Filter element is null", $"{path}[{i}]"), $"{path}[{i}]"))]);
    }

    private static OrderAst ParseOrder(JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("orderBy entry must be an object", path);

        var field = ParseFieldPath(obj["field"], $"{path}.field");

        var direction = SortDirection.Asc;
        if (obj["direction"] is { } dn)
        {
            direction = dn is JsonValue dv && dv.TryGetValue<string>(out var d)
                ? d switch
                {
                    "asc" => SortDirection.Asc,
                    "desc" => SortDirection.Desc,
                    _ => throw new QueryParseException("'direction' must be asc|desc", $"{path}.direction")
                }
                : throw new QueryParseException("'direction' must be a string", $"{path}.direction");
        }

        return new OrderAst(field, direction);
    }

    private static CursorAst ParseCursor(JsonNode node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("Cursor must be an object", path);
        if (obj["values"] is not JsonArray values)
            throw new QueryParseException("'values' is required and must be an array", $"{path}.values");
        if (obj["before"] is not JsonValue bv || !bv.TryGetValue<bool>(out var before))
            throw new QueryParseException("'before' is required and must be a boolean", $"{path}.before");

        return new CursorAst(
            [.. values.Select((v, i) => ParseValue(
                v ?? throw new QueryParseException("Cursor value is null", $"{path}.values[{i}]"),
                $"{path}.values[{i}]"))],
            before);
    }

    private static FilterOperator ParseOp(JsonNode? node, string path) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && Ops.TryGetValue(s, out var op)
            ? op : throw new QueryParseException("Unknown operator", path);

    private static FieldPath ParseFieldPath(JsonNode? node, string path)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
            throw new QueryParseException("'field' is required and must be a string", path);
        try { return FieldPath.Parse(s); }
        catch (ArgumentException ex) { throw new QueryParseException(ex.Message, path); }
    }

    private static Value ParseValue(JsonNode node, string path)
    {
        try { return ValueSerializer.Read(node); }
        catch (WireFormatException ex) { throw new QueryParseException(ex.Message, path); }
    }
}
