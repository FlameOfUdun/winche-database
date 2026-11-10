using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Documents;

namespace Winche.Database.Abstraction;

/// <summary>
/// Represents a hook that can be triggered on document operations such as set, update, or delete.
/// The path pattern that selects which documents fire this hook is supplied at registration time
/// (<see cref="DependencyInjection.HookBuilder.Add{THook}(string)"/>), not on the hook itself —
/// so the same hook behavior can be bound to different paths.
/// </summary>
public abstract class DocumentStoreHook
{
    /// <summary>
    /// Called for every change to a matching document. The default calls <see cref="OnDocumentSetAsync"/>,
    /// <see cref="OnDocumentUpdatedAsync"/> or <see cref="OnDocumentDeletedAsync"/>; override it to see the
    /// document before and after the change (<see cref="DocumentChange.Before"/> / <see cref="DocumentChange.After"/>),
    /// which are filled when the hook is registered <c>withSnapshots: true</c>.
    /// </summary>
    public virtual Task OnChangeAsync(DocumentChange change, CancellationToken ct) => change.Type switch
    {
        ChangeType.Added when change.After is { } added => OnDocumentSetAsync(change.Path, added, ct),
        ChangeType.Modified when change.After is { } modified => OnDocumentUpdatedAsync(change.Path, modified, ct),
        ChangeType.Removed => OnDocumentDeletedAsync(change.Path, ct),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// This method is called when a document is set. The hook can perform additional actions based on the document being set.
    /// </summary>
    /// <param name="path">The path of the document being set.</param>
    /// <param name="document">The document being set.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public virtual Task OnDocumentSetAsync(string path, Document document, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// This method is called when a document is updated. The hook can perform additional actions based on the updated document.
    /// </summary>
    /// <param name="path">The path of the document being updated.</param>
    /// <param name="document">The updated document.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public virtual Task OnDocumentUpdatedAsync(string path, Document document, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// This method is called when a document is deleted. The hook can perform additional actions based on the deleted document.
    /// </summary>
    /// <param name="path">The path of the document being deleted.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public virtual Task OnDocumentDeletedAsync(string path, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// One change to a document, as a hook sees it.
/// </summary>
/// <param name="Type">Added (created), Modified (updated) or Removed (deleted).</param>
/// <param name="Path">The document's path.</param>
/// <param name="Before">
/// The document before the change — null when it didn't exist (Added), or when the hook isn't registered
/// <c>withSnapshots</c> (or the change predates that registration).
/// </param>
/// <param name="After">
/// With snapshots: the document as this change wrote it (null when Removed). Without: its current state when
/// the hook runs (null if it's gone again), as before 10.5.0.
/// </param>
/// <param name="Version">The document's version after the change (for a removal, the version removed): an idempotency key.</param>
public sealed record DocumentChange(ChangeType Type, string Path, Document? Before, Document? After, long Version);
