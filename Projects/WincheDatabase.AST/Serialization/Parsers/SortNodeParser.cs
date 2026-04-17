using System.Text.Json.Nodes;
using WincheDatabase.AST.Models;

namespace WincheDatabase.AST.Serialization.Parsers
{
    internal static class SortNodeParser
    {
        public static List<SortNode> ParseArray(JsonArray? arr)
        {
            if (arr is null) 
                return [];

            return [.. arr.OfType<JsonObject>().Select(ParseObject)];
        }

        private static SortNode ParseObject(JsonObject obj)
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
