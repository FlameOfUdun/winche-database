using Winche.Database.Documents;
using Winche.Database.Runtime.Writes;

namespace Winche.Database.Authorization;

/// <summary>
/// Adapts the in-transaction <see cref="ITransactionalDocumentReader"/> to the rules engine's
/// <see cref="IRuleResourceProvider"/>, with a per-evaluation cache. Reads resolve against the same
/// transaction snapshot the write commits against.
/// </summary>
internal sealed class TransactionalRuleDocumentSource(ITransactionalDocumentReader reader) : CachingRuleResourceProvider
{
    protected override Task<Document?> FetchDocumentAsync(string path, CancellationToken ct) =>
        reader.GetAsync(path, ct);
}
