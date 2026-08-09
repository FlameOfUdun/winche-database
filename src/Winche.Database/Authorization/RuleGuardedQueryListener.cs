using System.Runtime.CompilerServices;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Listening;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Querying;

namespace Winche.Database.Authorization;

/// <summary>
/// <see cref="IQueryListener"/> decorator that re-runs the "rules are not filters" query analysis
/// (<see cref="QueryToConstraints"/> + <see cref="RuleEngine.Allows(QueryConstraints,RuleRequest)"/>)
/// before every emission, terminating the subscription with <see cref="AccessDeniedException"/> if
/// the query is no longer provably safe. Purely in-memory analysis — no database round-trip — so
/// re-checking on every emission is cheap.
/// </summary>
/// <remarks>
/// <paramref name="claims"/> is captured once at construction and reused for every re-evaluation —
/// connection claims are fixed at the HTTP upgrade and never refresh mid-connection, so there is no
/// fresher value to read. This means re-authorization does NOT catch token expiry or revocation;
/// that requires a connection-lifetime deadline and is out of scope here. What it DOES catch: the
/// current (possibly hot-swapped) ruleset and a fresh <c>request.time</c>.
/// </remarks>
public sealed class RuleGuardedQueryListener(
    IQueryListener inner,
    RuleEngine engine,
    IDocumentDatabase core,
    Query query,
    IReadOnlyDictionary<string, object?>? claims)
    : IQueryListener
{
    private readonly QueryConstraints _constraints = QueryToConstraints.Convert(query);
    private IAsyncEnumerator<QuerySnapshot>? _enumerator;

    public async IAsyncEnumerable<QuerySnapshot> Snapshots([EnumeratorCancellation] CancellationToken ct = default)
    {
        _enumerator ??= inner.Snapshots(ct).GetAsyncEnumerator(ct);
        while (await _enumerator.MoveNextAsync())
        {
            Authorize();
            yield return _enumerator.Current;
        }
    }

    private void Authorize()
    {
        var request = new RuleRequest
        {
            Request = RequestBuilder.Build(claims, "list", RuleValue.Null),
            // Fresh per evaluation, matching the get-side guard, even though the query analyzer
            // itself never calls get()/exists() — kept for symmetry and future-proofing.
            Provider = new PostgresDocumentSource(core),
        };
        if (!engine.Allows(_constraints, request))
            throw new AccessDeniedException(query.Collection, "list");
    }

    public Task<IReadOnlySet<string>> ExistingAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        inner.ExistingAsync(paths, ct);

    public async ValueTask DisposeAsync()
    {
        if (_enumerator is not null)
            await _enumerator.DisposeAsync();
        await inner.DisposeAsync();
    }
}
