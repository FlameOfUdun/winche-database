using Winche.Database.Documents;
using Winche.Rules;

namespace Winche.Database.Authorization;

/// <summary>
/// Converts a <see cref="Document"/> to the rules engine's <c>resource</c> map.
/// </summary>
/// <remarks>
/// The document's own fields are exposed at the top level — a rule reads <c>resource.ownerId</c>,
/// never <c>resource.data.ownerId</c>. The storage columns are added as reserved siblings so rules
/// can condition on them: the names in <see cref="ReservedResourceFields.Names"/> (<c>id</c>,
/// <c>path</c>, <c>collection</c>, <c>createdAt</c>, <c>updatedAt</c>, <c>version</c>), plus
/// <c>__name__</c>. A column takes precedence if a field shares its name.
/// </remarks>
internal static class DocumentToResource
{
    /// <summary>
    /// Names bound by <see cref="Convert"/> below. Kept as data purely so the drift guard can
    /// check it against <see cref="ReservedResourceFields.Names"/> once, at type initialization,
    /// rather than on every document conversion.
    /// </summary>
    private static readonly string[] BoundNames =
        ["id", "path", "collection", "createdAt", "updatedAt", "version", "__name__"];

    static DocumentToResource()
    {
        // Every name QueryToConstraints treats as reserved must actually be bound to a column
        // in Convert — otherwise the skip-list and the resource map would silently drift apart.
        foreach (var name in ReservedResourceFields.Names)
            if (!BoundNames.Contains(name))
                throw new InvalidOperationException(
                    $"'{name}' is in {nameof(ReservedResourceFields)} but is not bound by {nameof(DocumentToResource)}.");
    }

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
        // __name__ is the one reserved name query execution DOES resolve to a storage column
        // (document_path) rather than the data column — bind it here too so get/list agree on it.
        map["__name__"] = RuleValue.Path(document.Path);

        return RuleValue.Map(map);
    }
}
