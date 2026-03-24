using System.Collections.Immutable;

namespace WincheDb.DocumentStore.Models;

public sealed record QuerySnapshot
{
    public ImmutableHashSet<string> DocumentIds { get; init; } = [];
    public int Count => DocumentIds.Count;
}
