using Winche.Database.Documents;
using Winche.Rules;

namespace Winche.Database.Authorization;

/// <summary>
/// Converts a <see cref="Document"/> to a <see cref="RuleValue"/> resource map suitable for the rules engine.
/// </summary>
/// <remarks>
/// The produced map mirrors Firestore's <c>resource</c> binding:
/// <code>
/// {
///   "data"     : Map of all document fields (each field via <see cref="ValueToRuleValue"/>),
///   "id"       : String (last path segment),
///   "__name__" : Path (full document path)
/// }
/// </code>
/// </remarks>
internal static class DocumentToResource
{
    /// <summary>
    /// Builds a <see cref="RuleValue.Map"/> resource from <paramref name="document"/>.
    /// </summary>
    /// <param name="document">The document to convert.</param>
    /// <returns>A map with keys <c>data</c>, <c>id</c>, and <c>__name__</c>.</returns>
    public static RuleValue Convert(Document document)
    {
        var data = RuleValue.Map(
            document.Fields.ToDictionary(kv => kv.Key, kv => ValueToRuleValue.Convert(kv.Value)));

        var id = RuleValue.String(document.Id);
        var name = RuleValue.Path(document.Path);

        return RuleValue.Map(new Dictionary<string, RuleValue>
        {
            ["data"] = data,
            ["id"] = id,
            ["__name__"] = name,
        });
    }
}
