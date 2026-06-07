using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Winche.Database.Querying.Ast.Serialization;

public sealed class QueryAstJsonConverter : JsonConverter<QueryAst>
{
    public override QueryAst Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (JsonNode.Parse(ref reader) is not JsonObject obj)
            throw new JsonException("Query must be a JSON object");
        try { return QueryParser.Parse(obj); }
        catch (QueryParseException ex) { throw new JsonException(ex.Message, ex); }
    }

    public override void Write(Utf8JsonWriter writer, QueryAst value, JsonSerializerOptions options) =>
        QueryAstWriter.Write(value).WriteTo(writer);
}
