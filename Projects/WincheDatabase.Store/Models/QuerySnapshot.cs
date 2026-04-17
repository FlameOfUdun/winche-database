using System.Collections.Immutable;

namespace WincheDatabase.Store.Models;

public sealed record QuerySnapshot
{
    public ImmutableHashSet<string> DocumentIds { get; init; } = [];
    public int Count => DocumentIds.Count;
}
