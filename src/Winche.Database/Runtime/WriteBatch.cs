using Winche.Database.Documents;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Runtime;

/// <summary>Fluent write buffer; CommitAsync submits one atomic WriteAsync (validation happens there).</summary>
public sealed class WriteBatch(IDocumentDatabase db)
{
    private readonly List<Write> _writes = [];

    public int Count => _writes.Count;

    public WriteBatch Set(string path, IReadOnlyDictionary<string, Value> fields, bool merge = false,
        IReadOnlyList<FieldTransform>? transforms = null, Precondition? precondition = null)
    {
        _writes.Add(new SetWrite { Path = path, Fields = fields, Merge = merge, Transforms = transforms, Precondition = precondition });
        return this;
    }

    public WriteBatch Update(string path, IReadOnlyDictionary<FieldPath, Value> fields,
        IReadOnlyList<FieldTransform>? transforms = null, Precondition? precondition = null)
    {
        _writes.Add(new UpdateWrite { Path = path, Fields = fields, Transforms = transforms, Precondition = precondition });
        return this;
    }

    public WriteBatch Delete(string path, Precondition? precondition = null, bool cascade = false)
    {
        _writes.Add(new DeleteWrite { Path = path, Precondition = precondition, Cascade = cascade });
        return this;
    }

    public Task<IReadOnlyList<WriteResult>> CommitAsync(CancellationToken ct = default) =>
        db.WriteAsync(_writes, ct);
}
