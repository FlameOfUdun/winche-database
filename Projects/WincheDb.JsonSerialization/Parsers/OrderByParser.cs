using System.Text.Json.Nodes;
using WincheDb.Core.Ast;

namespace WincheDb.JsonSerialization.Parsers
{
    internal static class OrderByParser
    {
        public static List<SortNode> Parse(JsonArray? arr)
        {
            if (arr is null) 
                return [];

            return [.. arr.OfType<JsonObject>().Select(ParseOne)];
        }

        private static SortNode ParseOne(JsonObject obj)
        {
            var field = obj["field"]?.GetValue<string>()
                ?? throw new ArgumentException("OrderBy entry requires 'field'");

            var dir = obj["direction"]?.GetValue<string>()?.ToLower() == "desc"
                ? SortDirection.Desc
                : SortDirection.Asc;

            var type = obj["type"]?.GetValue<string>() is { Length: > 0 } c
                ? Enum.Parse<FieldType>(c, ignoreCase: true)
                : (FieldType?)null;

            return new SortNode(field, dir, type);
        }
    }
}
