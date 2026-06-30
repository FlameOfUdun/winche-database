using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Runtime.ChangeFeed;

namespace Winche.Database.Runtime.Listening;

/// <summary>
/// Native single-document registry: groups keyed by document path; the relevance bucket is also the
/// path. The initial snapshot is one point read; each relevant batch reads the changed document
/// straight from <see cref="ChangeBatch.Documents"/> (or treats a removal as absent) — no requery.
/// </summary>
public sealed class DocumentListenerRegistry(NpgsqlDataSource source)
    : LiveSubscriptionRegistry<DocumentListenerRegistry.DocumentGroup, DocumentSnapshot>(source)
{
    public IDocumentListener ListenToDocument(string path, ListenOptions? listenOptions = null)
    {
        if (!DocumentPathParser.IsValidDocumentPath(path, out var error))
            throw new RuntimeException(RuntimeStatus.InvalidArgument, error!);
        return (IDocumentListener)Subscribe(
            path, path,
            (key, indexKey) => new DocumentGroup { Key = key, IndexKey = indexKey },
            listenOptions);
    }

    // ── Hooks ──────────────────────────────────────────────────────────────────

    protected override SubscriptionHandleBase<DocumentSnapshot> CreateHandle(
        DocumentGroup group, Func<SubscriptionHandleBase<DocumentSnapshot>, ValueTask> onDispose) =>
        new DocumentHandle(onDispose);

    protected override async Task InitializeStateAsync(DocumentGroup group, CancellationToken ct)
    {
        await using var conn = await Source.OpenConnectionAsync(ct);
        group.Current = await new DocumentOperations(conn, null).GetAsync(group.Path, ct);
    }

    protected override bool IsRelevant(DocumentGroup group, ChangeBatch batch)
    {
        foreach (var record in batch.Records)
            if (record.Path == group.Path) return true;
        return false;
    }

    protected override Task<(DocumentSnapshot? Snapshot, bool Changed)> RecomputeAsync(
        DocumentGroup group, ChangeBatch batch, CancellationToken ct)
    {
        // The batch already carries the latest doc for added/modified paths; removal ⇒ absent.
        // Walk records in seq order; the last touching our path wins.
        Document? next = group.Current;
        var touched = false;
        foreach (var record in batch.Records)
        {
            if (record.Path != group.Path) continue;
            touched = true;
            next = record.Type == ChangeType.Removed ? null : batch.Documents.GetValueOrDefault(record.Path);
        }

        if (!touched) return Task.FromResult<(DocumentSnapshot?, bool)>((null, false));

        var unchanged = (group.Current is null && next is null)
            || (group.Current is not null && next is not null && group.Current.UpdateTime == next.UpdateTime);
        group.Current = next;
        if (unchanged) return Task.FromResult<(DocumentSnapshot?, bool)>((null, false));

        var snap = new DocumentSnapshot(next, next is not null, batch.Records[^1].CommitTime, batch.Records[^1].Seq);
        return Task.FromResult<(DocumentSnapshot?, bool)>((snap, true));
    }

    protected override DocumentSnapshot BuildInitialSnapshot(DocumentGroup group) =>
        new(group.Current, group.Current is not null, DateTimeOffset.UtcNow, group.LastSeq);

    protected override DocumentSnapshot BuildCurrentMarker(DocumentGroup group) =>
        new(null, false, DateTimeOffset.UtcNow, group.LastSeq, Current: true);

    protected override Task<bool> HasRelevantChangesAfterAsync(DocumentGroup group, long resumeSeq, CancellationToken ct) =>
        Reader.HasDocumentChangesAfterAsync(resumeSeq, group.Path, ct);

    protected override string IndexKeyOf(ChangeRecord record) => record.Path;

    // ── Group / handle ─────────────────────────────────────────────────────────

    public sealed class DocumentGroup : SubscriptionGroup<DocumentSnapshot>
    {
        /// <summary>The subscribed document path — always equal to <see cref="SubscriptionGroup{TSnapshot}.Key"/>.</summary>
        public string Path => Key;
        public Document? Current { get; set; }
    }

    private sealed class DocumentHandle(Func<SubscriptionHandleBase<DocumentSnapshot>, ValueTask> onDispose)
        : SubscriptionHandleBase<DocumentSnapshot>(onDispose), IDocumentListener;
}
