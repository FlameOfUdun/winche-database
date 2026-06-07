using Winche.Database.Documents;
using Winche.Database.Runtime.Listening;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

public class SnapshotDiffTests
{
    private static Document Doc(string id, long version = 1) => new()
    {
        Path = $"c/{id}", Id = id, Collection = "c",
        Fields = new Dictionary<string, Value>(),
        CreateTime = DateTimeOffset.UnixEpoch,
        UpdateTime = DateTimeOffset.UnixEpoch.AddSeconds(version),
        Version = version,
    };

    [Fact]
    public void EmptyToFull_AllAdded_WithNewIndices()
    {
        var changes = SnapshotDiff.Compute([], [Doc("a"), Doc("b")]);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(ListenChangeType.Added, c.Type));
        Assert.Equal((-1, 0), (changes[0].OldIndex, changes[0].NewIndex));
        Assert.Equal((-1, 1), (changes[1].OldIndex, changes[1].NewIndex));
    }

    [Fact]
    public void Removal_CarriesOldIndex()
    {
        var changes = SnapshotDiff.Compute([Doc("a"), Doc("b"), Doc("c")], [Doc("a"), Doc("c")]);
        var removed = Assert.Single(changes);
        Assert.Equal(ListenChangeType.Removed, removed.Type);
        Assert.Equal("b", removed.Document.Id);
        Assert.Equal((1, -1), (removed.OldIndex, removed.NewIndex));
    }

    [Fact]
    public void Modification_CarriesBothIndices_PositionMove()
    {
        // b modified (version bump) and moves from index 1 to index 0
        var old = new[] { Doc("a", 1), Doc("b", 1) };
        var @new = new[] { Doc("b", 2), Doc("a", 1) };
        var changes = SnapshotDiff.Compute(old, @new);
        var modified = Assert.Single(changes);
        Assert.Equal(ListenChangeType.Modified, modified.Type);
        Assert.Equal("b", modified.Document.Id);
        Assert.Equal((1, 0), (modified.OldIndex, modified.NewIndex));
        Assert.Equal(2, modified.Document.Version);                  // carries the NEW doc
    }

    [Fact]
    public void PureMove_WithoutContentChange_IsNotAChange()
    {
        // a and b swap positions because of c's arrival — only c is a change
        var old = new[] { Doc("a"), Doc("b") };
        var @new = new[] { Doc("c"), Doc("b"), Doc("a") };
        // (a and b unchanged versions; order differs)
        var changes = SnapshotDiff.Compute(old, [Doc("c"), old[1], old[0]]);
        var added = Assert.Single(changes);
        Assert.Equal("c", added.Document.Id);
    }

    [Fact]
    public void MixedChanges_OrderedRemovedAddedModified()
    {
        var old = new[] { Doc("a", 1), Doc("b", 1), Doc("d", 1) };
        var @new = new[] { Doc("b", 2), Doc("e", 1) };               // a,d removed; e added; b modified
        var changes = SnapshotDiff.Compute(old, @new);

        Assert.Equal(4, changes.Count);
        Assert.Equal(ListenChangeType.Removed, changes[0].Type);     // removals first, by OldIndex asc
        Assert.Equal("a", changes[0].Document.Id);
        Assert.Equal(ListenChangeType.Removed, changes[1].Type);
        Assert.Equal("d", changes[1].Document.Id);
        Assert.Equal(ListenChangeType.Added, changes[2].Type);       // then additions by NewIndex asc
        Assert.Equal("e", changes[2].Document.Id);
        Assert.Equal(ListenChangeType.Modified, changes[3].Type);    // then modifications by NewIndex asc
        Assert.Equal("b", changes[3].Document.Id);
    }

    [Fact]
    public void NoChanges_EmptyDiff()
    {
        var docs = new[] { Doc("a"), Doc("b") };
        Assert.Empty(SnapshotDiff.Compute(docs, docs));
    }
}
