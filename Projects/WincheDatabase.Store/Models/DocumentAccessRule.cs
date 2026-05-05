using WincheDatabase.Core.Models;
using WincheSentinel.Core.Abstraction;
using WincheSentinel.Core.Models;

namespace WincheDatabase.Store.Models;

public abstract class DocumentAccessRule : IAccessRule<Document>
{
    public abstract string Path { get; }

    public abstract IReadOnlySet<AccessOperation> Operations { get; }

    public abstract Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct);
}
