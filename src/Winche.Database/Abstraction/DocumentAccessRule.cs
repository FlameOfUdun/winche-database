using Winche.Database.Documents;
using Winche.Sentinel.Interfaces;
using Winche.Sentinel.Models;

namespace Winche.Database.Abstraction;

public abstract class DocumentAccessRule : IResourceAccessRule<Document>
{
    public abstract string Path { get; }

    public abstract IReadOnlySet<AccessOperation> Operations { get; }

    public abstract Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct);
}
