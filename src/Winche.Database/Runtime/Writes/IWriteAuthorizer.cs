using Winche.Database.Documents;

namespace Winche.Database.Runtime.Writes;

/// <summary>
/// One write about to be persisted, with its pre- and post-write document state.
/// <paramref name="Before"/> is the committed document before this write (null if it did not exist);
/// <paramref name="After"/> is the computed post-write document (null for a delete).
/// </summary>
public sealed record PendingWrite(Write Write, Document? Before, Document? After);

/// <summary>Reads documents within the in-flight write transaction (for rule <c>get()</c>/<c>exists()</c>).</summary>
public interface ITransactionalDocumentReader
{
    /// <summary>The document at <paramref name="path"/> read in the current transaction, or null.</summary>
    Task<Document?> GetAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// Authorizes a batch of writes from <b>inside</b> the write transaction, before persistence.
/// Implementations throw to deny (rolling back the whole transaction) and return to allow all.
/// <paramref name="commitTime"/> is the transaction's real commit timestamp.
/// </summary>
public interface IWriteAuthorizer
{
    Task AuthorizeAsync(
        IReadOnlyList<PendingWrite> writes,
        ITransactionalDocumentReader reader,
        DateTimeOffset commitTime,
        CancellationToken ct);
}
