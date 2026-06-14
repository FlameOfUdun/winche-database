using Npgsql;
using Winche.Database.Documents;

namespace Winche.Database.Runtime.Writes;

/// <summary>
/// <see cref="ITransactionalDocumentReader"/> backed by the write transaction's own connection and
/// transaction, so rule <c>get()</c>/<c>exists()</c> see the same snapshot the write commits against.
/// </summary>
internal sealed class TxDocumentReader(NpgsqlConnection conn, NpgsqlTransaction tx) : ITransactionalDocumentReader
{
    public Task<Document?> GetAsync(string path, CancellationToken ct = default) =>
        new DocumentOperations(conn, tx).GetAsync(path, ct);
}
