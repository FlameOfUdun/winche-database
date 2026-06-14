using Winche.Database.Runtime.Writes;
using Winche.Rules;
using Winche.Rules.Evaluation;

namespace Winche.Database.Authorization;

/// <summary>
/// Adapts the in-transaction <see cref="ITransactionalDocumentReader"/> to the rules engine's
/// <see cref="IRuleResourceProvider"/>, with a per-evaluation cache. Reads resolve against the same
/// transaction snapshot the write commits against.
/// </summary>
internal sealed class TransactionalRuleDocumentSource(ITransactionalDocumentReader reader) : IRuleResourceProvider
{
    private readonly Dictionary<string, RuleValue?> _cache = new(StringComparer.Ordinal);

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        await FetchAsync(path, ct) is not null;

    public async Task<RuleValue> GetAsync(string path, CancellationToken ct = default) =>
        await FetchAsync(path, ct) ?? RuleValue.Null;

    private async Task<RuleValue?> FetchAsync(string path, CancellationToken ct)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        var doc = await reader.GetAsync(path, ct);
        var value = doc is not null ? DocumentToResource.Convert(doc) : (RuleValue?)null;
        _cache[path] = value;
        return value;
    }
}
