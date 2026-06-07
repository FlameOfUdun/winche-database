using Winche.Database.Documents;

namespace Winche.Database.Abstraction;

/// <summary>
/// Represents a hook that can be triggered on document operations such as set, update, or delete.
/// </summary>
public abstract class DocumentStoreHook
{
    /// <summary>
    /// Gets the path pattern that this hook applies to. The hook will be triggered for any document whose path matches this pattern.
     /// The pattern can include wildcards (e.g., "users/*/profile") to match multiple documents.
    /// </summary>
    public abstract string Path { get; }

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
