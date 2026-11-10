using Winche.Database.Documents;
using Winche.Rules;

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
    public static RuleValue Convert(Document document)
    {
        var map = new Dictionary<string, RuleValue>(StringComparer.Ordinal);

        foreach (var (key, value) in document.Fields)
            map[key] = ValueToRuleValue.Convert(value);
        var data = RuleValue.Map(new Dictionary<string, RuleValue>(map, StringComparer.Ordinal));

        map["id"] = RuleValue.String(document.Id);
        map["path"] = RuleValue.Path(document.Path);
        map["collection"] = RuleValue.String(document.Collection);
        map["createdAt"] = RuleValue.Timestamp(document.CreateTime);
        map["updatedAt"] = RuleValue.Timestamp(document.UpdateTime);
        map["version"] = RuleValue.Int(document.Version);
        map.TryAdd("data", data);

        return RuleValue.Map(map);
    }
}
