using Winche.Database.Core.Models;
using WincheSentinel.Interfaces;
using WincheSentinel.Models;

namespace Winche.Database.Models;

public abstract class DocumentAccessRule : IResourceAccessRule<Document>
{
    public abstract string Path { get; }

    public abstract IReadOnlySet<AccessOperation> Operations { get; }

    public abstract Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct);
}
