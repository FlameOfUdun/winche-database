using Winche.Database.Documents;
using Winche.Sentinel.Interfaces;
using Winche.Sentinel.Models;

namespace Winche.Database.Abstraction;

/// <summary>
/// Represents an access control rule for documents in the database. 
/// </summary>
public abstract class DocumentAccessRule : IResourceAccessRule<Document>
{
    /// <inheritdoc/>
    public abstract string Path { get; }

    /// <inheritdoc/>
    public abstract IReadOnlySet<AccessOperation> Operations { get; }

    /// <inheritdoc/>
    public abstract Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct);
}
