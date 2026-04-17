using System.Text.Json.Nodes;
using WincheDatabase.AST.Models;

namespace WincheDatabase.AST.Serialization.Parsers
{
    internal static class PipelineStageParser
    {
        public static List<PipelineStage> ParseArray(JsonArray? arr)
        {
            if (arr is null)
                return [];

            return [.. arr.OfType<JsonObject>().Select(ParseObject)];
        }

        public static PipelineStage ParseObject(JsonObject obj)
        {
            if (obj.ContainsKey("$match")) return ParseMatch(obj["$match"]!.AsObject());
            if (obj.ContainsKey("$lookup")) return ParseLookup(obj["$lookup"]!.AsObject());
            if (obj.ContainsKey("$unwind")) return ParseUnwind(obj["$unwind"]!.AsObject());
            if (obj.ContainsKey("$group")) return ParseGroup(obj["$group"]!.AsObject());
            if (obj.ContainsKey("$project")) return ParseProject(obj["$project"]!.AsObject());
            if (obj.ContainsKey("$sort")) return ParseSort(obj["$sort"]!.AsObject());
            if (obj.ContainsKey("$limit")) return new LimitStage(obj["$limit"]!.GetValue<int>());
            if (obj.ContainsKey("$skip")) return new SkipStage(obj["$skip"]!.GetValue<int>());

            throw new NotSupportedException($"Unknown pipeline stage: {obj.ToJsonString()}");
        }

        private static MatchStage ParseMatch(JsonObject obj)
        {
            var collection = obj["collection"]!.GetValue<string>();
            var filter = obj["filter"] is JsonObject f ? WhereNodeParser.Parse(f) : null;
            return new MatchStage(collection, filter);
        }

        private static LookupStage ParseLookup(JsonObject obj)
        {
            var filter = obj["filter"] is JsonObject f ? WhereNodeParser.Parse(f) : null;
            var orderBy = obj["orderBy"] is JsonArray a ? SortNodeParser.ParseArray(a) : null;

            return new LookupStage(
                Collection: obj["from"]!.GetValue<string>(),
                LocalField: obj["localField"]!.GetValue<string>(),
                ForeignField: obj["foreignField"]!.GetValue<string>(),
                As: obj["as"]!.GetValue<string>(),
                Filter: filter,
                OrderBy: orderBy,
                Limit: obj["limit"]?.GetValue<int>() ?? 100
            );
        }

        private static UnwindStage ParseUnwind(JsonObject obj)
        {
            return new(
                Field: obj["path"]!.GetValue<string>(),
                As: obj["includeArrayIndex"]!.GetValue<string>(),
                PreserveNullAndEmpty: obj["preserveNullAndEmptyArrays"]?.GetValue<bool>() ?? false
            );
        }

        private static GroupStage ParseGroup(JsonObject obj)
        {
            var keys = (obj["keys"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(ParseGroupKey)
                .ToList();

            var accumulators = (obj["accumulators"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(ParseAccumulatorField)
                .ToList();

            var having = obj["having"] is JsonObject h ? WhereNodeParser.Parse(h) : null;

            return new GroupStage(keys, accumulators, having);
        }

        private static GroupKey ParseGroupKey(JsonObject obj)
        {
            return new(
                As: obj["as"]!.GetValue<string>(),
                Field: obj["field"]!.GetValue<string>(),
                Type: ParseFieldType(obj["type"]?.GetValue<string>())
            );
        }

        private static AccumulatorField ParseAccumulatorField(JsonObject obj)
        {
            return new(
                As: obj["as"]!.GetValue<string>(),
                Function: ParseAggFunction(obj["function"]!.GetValue<string>()),
                Field: obj["field"]?.GetValue<string>(),
                Type: ParseFieldType(obj["type"]?.GetValue<string>())
            );
        }

        private static ProjectStage ParseProject(JsonObject obj)
        {
            var fields = (obj["fields"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(ParseProjectField)
                .ToList();

            return new ProjectStage(fields);
        }

        private static ProjectField ParseProjectField(JsonObject obj)
        {
            return new(
                As: obj["as"]!.GetValue<string>(),
                Expression: ParseProjectExpr(obj["expression"]!.AsObject())
            );
        }

        private static ProjectExpr ParseProjectExpr(JsonObject obj)
        {
            if (obj.ContainsKey("field") && !obj.ContainsKey("function"))
                return new FieldRefExpr(
                    Field: obj["field"]!.GetValue<string>(),
                    Type: ParseFieldType(obj["type"]?.GetValue<string>())
                );

            if (obj.ContainsKey("value"))
                return new LiteralExpr(obj["value"]?.GetValue<object>());

            if (obj.ContainsKey("function"))
                return new AggFuncExpr(
                    Function: ParseAggFunction(obj["function"]!.GetValue<string>()),
                    Field: obj["field"]?.GetValue<string>(),
                    Type: ParseFieldType(obj["type"]?.GetValue<string>())
                );

            throw new NotSupportedException($"Unknown ProjectExpr: {obj.ToJsonString()}");
        }

        private static SortStage ParseSort(JsonObject obj)
        {
            var fields = SortNodeParser.ParseArray(obj["fields"]!.AsArray());
            return new SortStage(fields);
        }

        private static FieldType? ParseFieldType(string? symbol)
        {
            return symbol is null ? null : Enum.TryParse<FieldType>(symbol, ignoreCase: true, out var t) ? t : null;
        }

        private static AggFunction ParseAggFunction(string symbol)
        {
            return symbol switch
            {
                "$count" => AggFunction.Count,
                "$sum" => AggFunction.Sum,
                "$avg" => AggFunction.Avg,
                "$min" => AggFunction.Min,
                "$max" => AggFunction.Max,
                "$push" => AggFunction.Push,
                "$addToSet" => AggFunction.AddToSet,
                "$first" => AggFunction.First,
                "$last" => AggFunction.Last,
                _ => throw new NotSupportedException($"Unknown aggregation function: {symbol}")
            };
        }
    }
}
