namespace Winche.Database.Authorization;

/// <summary>
/// The reserved keys that <see cref="DocumentToResource"/> binds to storage columns in the rules
/// engine's <c>resource</c> map, overriding any user field of the same name.
/// </summary>
/// <remarks>
/// Query execution resolves these same names against the user-writable data column (see
/// <c>SchemaResolver</c> and <c>FilterEvaluator</c>), not the storage columns — so a query filter on
/// one of these names would disagree with what a rule proves against <see cref="DocumentToResource"/>.
/// <see cref="QueryToConstraints"/> reads this set to skip such filters when proving a query safe.
/// Adding a name here means adding it to both sides — keep the two in sync.
/// </remarks>
internal static class ReservedResourceFields
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "id",
        "path",
        "collection",
        "createdAt",
        "updatedAt",
        "version",
    };
}
