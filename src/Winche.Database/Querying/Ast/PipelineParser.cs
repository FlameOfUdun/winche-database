// src/Winche.Database/Querying/Ast/PipelineParser.cs
using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast;

/// <summary>Wire JSON → PipelineAst. Shape only; semantic rules live in PipelineNormalizer.</summary>
public static class PipelineParser
{
    private static readonly Dictionary<string, AggFunction> Fns = new(StringComparer.Ordinal)
    {
        ["count"] = AggFunction.Count, ["sum"] = AggFunction.Sum, ["avg"] = AggFunction.Avg,
        ["min"] = AggFunction.Min, ["max"] = AggFunction.Max, ["push"] = AggFunction.Push,
        ["addToSet"] = AggFunction.AddToSet, ["first"] = AggFunction.First, ["last"] = AggFunction.Last,
    };

    private static readonly string[] StageKeys =
        ["match", "filter", "lookup", "unwind", "group", "project", "sort", "limit", "skip"];

    public static PipelineAst Parse(JsonObject json)
    {
        if (json["pipeline"] is not JsonArray stages)
            throw new QueryParseException("'pipeline' is required and must be an array", "$.pipeline");

        return new PipelineAst([.. stages.Select((s, i) => ParseStage(
            s ?? throw new QueryParseException("Stage is null", $"$.pipeline[{i}]"), $"$.pipeline[{i}]"))]);
    }

    private static StageAst ParseStage(JsonNode node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("Stage must be an object", path);

        var keys = StageKeys.Where(obj.ContainsKey).ToList();
        if (keys.Count != 1)
            throw new QueryParseException(
                $"Stage must have exactly one of: {string.Join(", ", StageKeys)}", path);

        if (obj.Select(p => p.Key).Any(k => !StageKeys.Contains(k)))
            throw new QueryParseException("Stage contains unknown keys", path);

        var key = keys[0];
        var body = obj[key];
        var p = $"{path}.{key}";

        return key switch
        {
            "match" => ParseMatch(body, p),
            "filter" => new FilterStageAst(QueryParser.ParseFilter(
                body ?? throw new QueryParseException("'filter' must be a filter object", p), p)),
            "lookup" => ParseLookup(body, p),
            "unwind" => ParseUnwind(body, p),
            "group" => ParseGroup(body, p),
            "project" => ParseProject(body, p),
            "sort" => ParseSort(body, p),
            "limit" => new LimitStageAst(GetInt(body, p)),
            "skip" => new SkipStageAst(GetInt(body, p)),
            _ => throw new QueryParseException($"Unknown stage '{key}'", path),
        };
    }

    private static MatchStageAst ParseMatch(JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("'match' must be an object", path);
        var collection = GetString(obj["collection"], $"{path}.collection");
        var where = obj["where"] is { } w ? QueryParser.ParseFilter(w, $"{path}.where") : null;
        return new MatchStageAst(collection, where);
    }

    private static LookupStageAst ParseLookup(JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("'lookup' must be an object", path);

        var limit = 100;
        if (obj["limit"] is { } ln) limit = GetInt(ln, $"{path}.limit");

        IReadOnlyList<OrderAst>? orderBy = null;
        if (obj["orderBy"] is { } ob)
        {
            if (ob is not JsonArray arr)
                throw new QueryParseException("'orderBy' must be an array", $"{path}.orderBy");
            orderBy = [.. arr.Select((n, i) => ParseOrder(n, $"{path}.orderBy[{i}]"))];
        }

        return new LookupStageAst(
            GetString(obj["collection"], $"{path}.collection"),
            GetFieldPath(obj["localField"], $"{path}.localField"),
            GetFieldPath(obj["foreignField"], $"{path}.foreignField"),
            GetString(obj["as"], $"{path}.as"),
            obj["where"] is { } w ? QueryParser.ParseFilter(w, $"{path}.where") : null,
            orderBy,
            limit);
    }

    private static UnwindStageAst ParseUnwind(JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("'unwind' must be an object", path);

        var preserve = false;
        if (obj["preserveNullAndEmpty"] is { } pn)
        {
            if (pn is not JsonValue pv || !pv.TryGetValue<bool>(out preserve))
                throw new QueryParseException("'preserveNullAndEmpty' must be a boolean", $"{path}.preserveNullAndEmpty");
        }

        return new UnwindStageAst(
            GetFieldPath(obj["field"], $"{path}.field"),
            GetString(obj["as"], $"{path}.as"),
            preserve);
    }

    private static GroupStageAst ParseGroup(JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("'group' must be an object", path);

        var keys = new List<GroupKeyAst>();
        if (obj["keys"] is { } kn)
        {
            if (kn is not JsonArray karr)
                throw new QueryParseException("'keys' must be an array", $"{path}.keys");
            keys.AddRange(karr.Select((k, i) => ParseGroupKey(k, $"{path}.keys[{i}]")));
        }

        if (obj["accumulators"] is not JsonArray aarr)
            throw new QueryParseException("'accumulators' is required and must be an array", $"{path}.accumulators");
        var accs = aarr.Select((a, i) => ParseAccumulator(a, $"{path}.accumulators[{i}]")).ToList();

        var having = obj["having"] is { } h ? QueryParser.ParseFilter(h, $"{path}.having") : null;
        return new GroupStageAst(keys, accs, having);
    }

    private static GroupKeyAst ParseGroupKey(JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("Group key must be an object", path);
        return new GroupKeyAst(GetString(obj["as"], $"{path}.as"), GetFieldPath(obj["field"], $"{path}.field"));
    }

    private static AccumulatorAst ParseAccumulator(JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("Accumulator must be an object", path);

        var fnStr = GetString(obj["fn"], $"{path}.fn");
        if (!Fns.TryGetValue(fnStr, out var fn))
            throw new QueryParseException($"Unknown aggregate function '{fnStr}'", $"{path}.fn");

        FieldPath? field = obj["field"] is { } f ? GetFieldPath(f, $"{path}.field") : null;
        return new AccumulatorAst(GetString(obj["as"], $"{path}.as"), fn, field);
    }

    private static ProjectStageAst ParseProject(JsonNode? node, string path)
    {
        if (node is not JsonObject obj || obj["fields"] is not JsonArray arr)
            throw new QueryParseException("'project' must be an object with a 'fields' array", path);

        return new ProjectStageAst([.. arr.Select((f, i) => ParseProjection(f, $"{path}.fields[{i}]"))]);
    }

    private static ProjectionAst ParseProjection(JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("Projection must be an object", path);

        var As = GetString(obj["as"], $"{path}.as");
        var hasField = obj.ContainsKey("field");
        var hasValue = obj.ContainsKey("value");
        var hasFn = obj.ContainsKey("fn");

        if (hasFn)
        {
            var fnStr = GetString(obj["fn"], $"{path}.fn");
            if (!Fns.TryGetValue(fnStr, out var fn))
                throw new QueryParseException($"Unknown aggregate function '{fnStr}'", $"{path}.fn");
            FieldPath? field = obj["field"] is { } f ? GetFieldPath(f, $"{path}.field") : null;
            return new ProjectionAst(As, new AggFuncExprAst(fn, field));
        }
        if (hasValue && !hasField)
        {
            var valueNode = obj["value"] ?? throw new QueryParseException("'value' is null", $"{path}.value");
            try { return new ProjectionAst(As, new LiteralExprAst(ValueSerializer.Read(valueNode))); }
            catch (WireFormatException ex) { throw new QueryParseException(ex.Message, $"{path}.value"); }
        }
        if (hasField && !hasValue)
            return new ProjectionAst(As, new FieldRefExprAst(GetFieldPath(obj["field"], $"{path}.field")));

        throw new QueryParseException("Projection must be exactly one of: field | value | fn(+field)", path);
    }

    private static SortStageAst ParseSort(JsonNode? node, string path)
    {
        if (node is not JsonArray arr)
            throw new QueryParseException("'sort' must be an array", path);
        return new SortStageAst([.. arr.Select((n, i) => ParseOrder(n, $"{path}[{i}]"))]);
    }

    private static OrderAst ParseOrder(JsonNode? node, string path)
    {
        if (node is not JsonObject obj)
            throw new QueryParseException("Sort entry must be an object", path);

        var direction = SortDirection.Asc;
        if (obj["direction"] is { } dn)
        {
            direction = GetString(dn, $"{path}.direction") switch
            {
                "asc" => SortDirection.Asc,
                "desc" => SortDirection.Desc,
                _ => throw new QueryParseException("'direction' must be asc|desc", $"{path}.direction"),
            };
        }
        return new OrderAst(GetFieldPath(obj["field"], $"{path}.field"), direction);
    }

    private static string GetString(JsonNode? node, string path) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s : throw new QueryParseException("Required string is missing or empty", path);

    private static int GetInt(JsonNode? node, string path) =>
        node is JsonValue v && v.TryGetValue<int>(out var i)
            ? i : throw new QueryParseException("Must be an integer", path);

    private static FieldPath GetFieldPath(JsonNode? node, string path)
    {
        var s = GetString(node, path);
        try { return FieldPath.Parse(s); }
        catch (ArgumentException ex) { throw new QueryParseException(ex.Message, path); }
    }
}
