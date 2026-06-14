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
/// </remarks>
internal static class DocumentToResource
{
    public static RuleValue Convert(Document document)
    {
        var map = new Dictionary<string, RuleValue>(StringComparer.Ordinal);

        foreach (var (key, value) in document.Fields)
            map[key] = ValueToRuleValue.Convert(value);

        map["id"] = RuleValue.String(document.Id);
        map["path"] = RuleValue.Path(document.Path);
        map["collection"] = RuleValue.String(document.Collection);
        map["createdAt"] = RuleValue.Timestamp(document.CreateTime);
        map["updatedAt"] = RuleValue.Timestamp(document.UpdateTime);
        map["version"] = RuleValue.Int(document.Version);

        return RuleValue.Map(map);
    }
}
