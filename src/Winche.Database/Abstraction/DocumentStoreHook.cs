using Winche.Database.Core.Models;

namespace Winche.Database.Abstraction;

public abstract class DocumentStoreHook
{
    public abstract string Path { get; }

    public virtual Task OnDocumentSetAsync(string path, Document document, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnDocumentUpdatedAsync(string path, Document document, CancellationToken ct) => Task.CompletedTask;
    public virtual Task OnDocumentDeletedAsync(string path, CancellationToken ct) => Task.CompletedTask;
}
