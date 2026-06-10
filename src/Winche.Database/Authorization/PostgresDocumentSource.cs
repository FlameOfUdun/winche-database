using Winche.Database.Runtime;
using Winche.Rules;
using Winche.Rules.Evaluation;

namespace Winche.Database.Authorization;

/// <summary>
/// <see cref="IDocumentSource"/> that resolves <c>get()</c>/<c>exists()</c> calls in rules by reading from
/// the <b>core, unguarded</b> <see cref="IDocumentDatabase"/>. Maintains a per-instance cache so that
/// repeated reads for the same path within one authorization check hit the database only once.
/// </summary>
/// <remarks>
/// A new instance should be created per authorization request (e.g. per HTTP request / write operation)
/// so the cache stays bounded and does not leak between unrelated requests.
/// </remarks>
internal sealed class PostgresDocumentSource : IDocumentSource
{
    private readonly IDocumentDatabase _core;

    // Cache keyed by path; null entry = document does not exist.
    private readonly Dictionary<string, RuleValue?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initialises the source with the core (unguarded) document database.
    /// </summary>
    /// <param name="core">
    /// The rule-free <see cref="IDocumentDatabase"/> (typically <see cref="DocumentDatabase"/>).
    /// Must NOT be a guarded database to avoid infinite recursion during authorization.
    /// </param>
    public PostgresDocumentSource(IDocumentDatabase core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var resource = await FetchAsync(path, ct);
        return resource is not null;
    }

    /// <inheritdoc/>
    public async Task<RuleValue> GetAsync(string path, CancellationToken ct = default)
    {
        var resource = await FetchAsync(path, ct);
        return resource ?? RuleValue.Null;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cached resource map for <paramref name="path"/>, or fetches it from the core database
    /// and caches the result. Returns <c>null</c> when the document does not exist.
    /// </summary>
    private async Task<RuleValue?> FetchAsync(string path, CancellationToken ct)
    {
        if (_cache.TryGetValue(path, out var cached))
            return cached;

        var doc = await _core.GetAsync(path, ct);
        var value = doc is not null ? DocumentToResource.Convert(doc) : (RuleValue?)null;
        _cache[path] = value;
        return value;
    }
}
