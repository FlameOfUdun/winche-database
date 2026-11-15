using System.Globalization;
using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Values;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// The stored form of a document before / after a change (the feed's <c>before</c> / <c>after</c> columns):
/// <c>{"fields": {…storage form…}, "createdAt": …, "updatedAt": …, "version": n}</c>.
/// </summary>
internal static class ChangeSnapshots
{
    public static string Encode(IReadOnlyDictionary<string, Value> fields, DateTimeOffset createdAt, DateTimeOffset updatedAt, long version) =>
        new JsonObject
        {
            ["fields"] = JsonNode.Parse(StorageCodec.Encode(fields)),
            ["createdAt"] = createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["updatedAt"] = updatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["version"] = version,
        }.ToJsonString();

    public static Document Decode(string path, string json)
    {
        var obj = JsonNode.Parse(json)!.AsObject();
        var info = DocumentPathParser.ParsePath(path);
        return new Document
        {
            Path = path,
            Id = info.Id!,
            Collection = info.Collection,
            Fields = StorageCodec.Decode(obj["fields"]!.ToJsonString()),
            CreateTime = DateTimeOffset.Parse(obj["createdAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdateTime = DateTimeOffset.Parse(obj["updatedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Version = obj["version"]!.GetValue<long>(),
        };
    }
}
