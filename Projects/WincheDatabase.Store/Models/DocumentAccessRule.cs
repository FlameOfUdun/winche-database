using WincheDatabase.Core.Models;
using WincheSentinel.Core.Abstraction;
using WincheSentinel.Core.Models;

namespace WincheDatabase.Store.Models;

public sealed class DocumentAccessRule(
    string path, 
    IEnumerable<AccessOperation> operations, 
    Func<AccessContext<Document>, CancellationToken, Task<bool>> evaluate
) : IAccessRule<Document>
{
    private readonly string _path = path;
    private readonly IReadOnlySet<AccessOperation> _operations = operations.ToHashSet();
    private readonly Func<AccessContext<Document>, CancellationToken, Task<bool>> _evaluate = evaluate;

    public string Path => _path;

    public IReadOnlySet<AccessOperation> Operations => _operations;

    public Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct) => _evaluate(context, ct);
}
