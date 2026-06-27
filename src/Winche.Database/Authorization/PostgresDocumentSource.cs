using Winche.Database.Documents;
using Winche.Database.Runtime;

namespace Winche.Database.Authorization;

/// <summary>
/// <see cref="IRuleResourceProvider"/> that resolves <c>get()</c>/<c>exists()</c> calls in rules by reading from
/// the <b>core, unguarded</b> <see cref="IDocumentDatabase"/>. Maintains a per-instance cache so that
/// repeated reads for the same path within one authorization check hit the database only once.
/// </summary>
/// <remarks>
/// A new instance should be created per authorization request (e.g. per HTTP request / write operation)
/// so the cache stays bounded and does not leak between unrelated requests.
/// </remarks>
internal sealed class PostgresDocumentSource : CachingRuleResourceProvider
{
    private readonly IDocumentDatabase _core;

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

    protected override Task<Document?> FetchDocumentAsync(string path, CancellationToken ct) =>
        _core.GetAsync(path, ct);
}
