using Winche.Database.Abstraction;
using Winche.Database.Core.Models;

namespace Winche.Database.Sample.Configurations;

public class DocumentUpdateHook : DocumentStoreHook
{
    public override string Path => "**";

    public override Task OnDocumentSetAsync(string path, Document document, CancellationToken ct)
    {
        Console.WriteLine($"Document set: {path}");
        return Task.FromResult(0);
    }

    public override Task OnDocumentUpdatedAsync(string path, Document document, CancellationToken ct)
    {
        Console.WriteLine($"Document updated: {path}");
        return Task.FromResult(0);
    }

    public override Task OnDocumentDeletedAsync(string path, CancellationToken ct)
    {
        Console.WriteLine($"Document deleted: {path}");
        return Task.FromResult(0);
    }
}
