using System.Text.Json.Nodes;
using Winche.Values;

namespace Winche.Database.Documents;

/// <summary>
/// Documents as plain JSON, for webhook receivers and functions: numbers, strings, timestamps as ISO text, references
/// as paths, bytes as base64.
/// </summary>
public static class Plain
{
    public static JsonObject? Of(Document? d) => d is null ? null : new JsonObject
    {
        ["path"] = d.Path, ["id"] = d.Id, ["collection"] = d.Collection,
        ["createTime"] = d.CreateTime.ToString("O"), ["updateTime"] = d.UpdateTime.ToString("O"), ["fields"] = PlainJson.Fields(d.Fields),
    };
}
