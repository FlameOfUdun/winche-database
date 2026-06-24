using Winche.Database.Documents;

namespace Winche.Database.Runtime.Ttl;

/// <summary>
/// A TTL policy: documents in the collection group <paramref name="CollectionId"/> are deleted once
/// their <paramref name="Field"/> (a timestampValue) is in the past. A missing or non-timestamp field
/// never expires. Applies per collection group (all collections sharing the id).
/// </summary>
public sealed record TtlPolicy(string CollectionId, FieldPath Field)
{
    public static TtlPolicy For(string collectionId, string field) => new(collectionId, FieldPath.Parse(field));
}
