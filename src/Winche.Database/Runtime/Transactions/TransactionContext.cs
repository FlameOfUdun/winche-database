using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Transactions;

/// <summary>
/// The RunTransactionAsync body context: reads delegate to the database's transactional
/// reads (recording versions); Set/Update/Delete BUFFER writes for the commit. Firestore's
/// reads-before-writes rule: any read after the first buffered write → INVALID_ARGUMENT.
/// </summary>
public sealed class TransactionContext
{
    private readonly IDocumentDatabase _db;
    private readonly List<Write> _writes = [];

    internal TransactionContext(IDocumentDatabase db, string transactionId)
    {
        _db = db;
        TransactionId = transactionId;
    }

    public string TransactionId { get; }
    internal IReadOnlyList<Write> BufferedWrites => _writes;

    public Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        EnsureNoWritesYet();
        return _db.GetAsync(TransactionId, path, ct);
    }

    public async Task<IReadOnlyList<Document?>> GetAllAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        EnsureNoWritesYet();
        var docs = new List<Document?>(paths.Count);
        foreach (var p in paths)
            docs.Add(await _db.GetAsync(TransactionId, p, ct));
        return docs;
    }

    public Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default)
    {
        EnsureNoWritesYet();
        return _db.QueryAsync(TransactionId, query, ct);
    }

    public TransactionContext Set(string path, IReadOnlyDictionary<string, Value> fields, bool merge = false,
        IReadOnlyList<FieldTransform>? transforms = null, Precondition? precondition = null)
    {
        _writes.Add(new SetWrite { Path = path, Fields = fields, Merge = merge, Transforms = transforms, Precondition = precondition });
        return this;
    }

    public TransactionContext Update(string path, IReadOnlyDictionary<FieldPath, Value> fields,
        IReadOnlyList<FieldTransform>? transforms = null, Precondition? precondition = null)
    {
        _writes.Add(new UpdateWrite { Path = path, Fields = fields, Transforms = transforms, Precondition = precondition });
        return this;
    }

    public TransactionContext Delete(string path, Precondition? precondition = null, bool cascade = false)
    {
        _writes.Add(new DeleteWrite { Path = path, Precondition = precondition, Cascade = cascade });
        return this;
    }

    private void EnsureNoWritesYet()
    {
        if (_writes.Count > 0)
            throw new RuntimeException(RuntimeStatus.InvalidArgument,
                "Transactions require all reads to happen before any writes.");
    }
}
