namespace Winche.Database.Values;

/// <summary>
/// Firestore wire-format tag constants shared by storage encoding and SQL projection.
/// Centralises the two JSONB structural keys so their spelling lives in one place.
/// </summary>
internal static class WireTags
{
    public const string MapValue = "mapValue";
    public const string Fields   = "fields";
}
