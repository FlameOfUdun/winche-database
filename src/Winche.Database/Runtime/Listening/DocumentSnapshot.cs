using System.Runtime.CompilerServices;
using Winche.Database.Documents;

namespace Winche.Database.Runtime.Listening;

/// <summary>Single-document listener snapshot (single-document snapshot contract).</summary>
public sealed record DocumentSnapshot(Document? Document, bool Exists, DateTimeOffset ReadTime, long ResumeToken);

/// <summary>A live single-document subscription.</summary>
public interface IDocumentListener : IAsyncDisposable
{
    IAsyncEnumerable<DocumentSnapshot> Snapshots(CancellationToken ct = default);
}

/// <summary>
/// Adapts an <see cref="IQueryListener"/> over a one-document (__name__ == path) query into
/// single-document snapshots: an empty result is "absent", a one-element result is "present".
/// </summary>
internal sealed class DocumentListener(IQueryListener inner) : IDocumentListener
{
    public async IAsyncEnumerable<DocumentSnapshot> Snapshots([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var s in inner.Snapshots(ct))
            // A __name__==path query yields 0 or 1 result, so [0] is safe by construction.
            yield return s.Documents.Count == 0
                ? new DocumentSnapshot(null, false, s.ReadTime, s.ResumeToken)
                : new DocumentSnapshot(s.Documents[0], true, s.ReadTime, s.ResumeToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
