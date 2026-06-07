using Winche.Database.Documents;

namespace Winche.Database.Runtime.Listening;

/// <summary>
/// Ordered diff between two query snapshots (spec §4 docChanges contract):
/// removed (old order) → added (new order) → modified (new order). Identity = path;
/// modified = same path with a different UpdateTime; pure position moves are not changes.
/// </summary>
public static class SnapshotDiff
{
    public static IReadOnlyList<DocumentChangeInfo> Compute(
        IReadOnlyList<Document> old, IReadOnlyList<Document> @new)
    {
        var oldIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < old.Count; i++) oldIndex[old[i].Path] = i;
        var newIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var j = 0; j < @new.Count; j++) newIndex[@new[j].Path] = j;

        var removed = new List<DocumentChangeInfo>();
        var added = new List<DocumentChangeInfo>();
        var modified = new List<DocumentChangeInfo>();

        for (var i = 0; i < old.Count; i++)
            if (!newIndex.ContainsKey(old[i].Path))
                removed.Add(new DocumentChangeInfo(ListenChangeType.Removed, old[i], i, -1));

        for (var j = 0; j < @new.Count; j++)
        {
            var doc = @new[j];
            if (!oldIndex.TryGetValue(doc.Path, out var i))
                added.Add(new DocumentChangeInfo(ListenChangeType.Added, doc, -1, j));
            else if (old[i].UpdateTime != doc.UpdateTime)
                modified.Add(new DocumentChangeInfo(ListenChangeType.Modified, doc, i, j));
        }

        return [.. removed, .. added, .. modified];
    }
}
