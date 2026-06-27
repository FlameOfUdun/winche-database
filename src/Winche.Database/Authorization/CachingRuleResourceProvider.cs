using Winche.Database.Documents;
using Winche.Rules;
using Winche.Rules.Evaluation;

namespace Winche.Database.Authorization;

/// <summary>
/// Base <see cref="IRuleResourceProvider"/> that resolves <c>get()</c>/<c>exists()</c> calls in rules by
/// fetching documents and converting them to rule resources, with a per-instance cache so repeated reads
/// for the same path within one authorization check hit the source only once. Create a new instance per
/// authorization request so the cache stays bounded and does not leak between unrelated requests.
/// Subclasses supply only the document fetch.
/// </summary>
internal abstract class CachingRuleResourceProvider : IRuleResourceProvider
{
    // Cache keyed by path; null entry = document does not exist.
    private readonly Dictionary<string, RuleValue?> _cache = new(StringComparer.Ordinal);

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default) =>
        await FetchAsync(path, ct) is not null;

    public async Task<RuleValue> GetAsync(string path, CancellationToken ct = default) =>
        await FetchAsync(path, ct) ?? RuleValue.Null;

    private async Task<RuleValue?> FetchAsync(string path, CancellationToken ct)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        var doc = await FetchDocumentAsync(path, ct);
        var value = doc is not null ? DocumentToResource.Convert(doc) : (RuleValue?)null;
        _cache[path] = value;
        return value;
    }

    /// <summary>Fetches the raw document for <paramref name="path"/>, or null if it does not exist.</summary>
    protected abstract Task<Document?> FetchDocumentAsync(string path, CancellationToken ct);
}
