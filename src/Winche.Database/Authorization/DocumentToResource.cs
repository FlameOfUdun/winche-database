using Winche.Database.Documents;
using Winche.Rules;
using Winche.Values;

namespace Winche.Database.Authorization;

/// <summary>
/// Converts a <see cref="Document"/> to the rules engine's <c>resource</c> map.
/// </summary>
/// <remarks>
/// The document's own fields are exposed at the top level — a rule reads <c>resource.ownerId</c>,
/// never <c>resource.data.ownerId</c>. The storage columns are added as reserved siblings so rules
/// can condition on them: <c>id</c>, <c>path</c>, <c>collection</c>, <c>createdAt</c>,
/// <c>updatedAt</c>, <c>version</c>. A column takes precedence if a field shares its name.
/// <para><c>data</c> is the document's fields alone, without the columns — what <c>keys()</c> and
/// <c>diff()</c> need: <c>request.resource.data.keys().hasOnly([...])</c>,
/// <c>request.resource.data.diff(resource.data).affectedKeys()</c>. A field that is itself named
/// <c>data</c> takes precedence, so documents and rules from before 10.5.0 read the same.</para>
/// </remarks>
internal static class DocumentToResource
{
    public static MapValue Convert(Document document)
    {
        // The stored values are the rule values: no copy of the data, only the top-level map with the metadata.
        var map = new Dictionary<string, Value>(document.Fields, StringComparer.Ordinal)
        {
            ["id"] = new StringValue(document.Id),
            ["path"] = new ReferenceValue(document.Path),
            ["collection"] = new StringValue(document.Collection),
            ["createdAt"] = new TimestampValue(document.CreateTime),
            ["updatedAt"] = new TimestampValue(document.UpdateTime),
            ["version"] = new IntegerValue(document.Version),
        };
        map.TryAdd("data", new MapValue(document.Fields));
        return new MapValue(map);
    }
}
