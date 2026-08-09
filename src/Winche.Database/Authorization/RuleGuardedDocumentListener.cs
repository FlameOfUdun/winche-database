using System.Runtime.CompilerServices;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Listening;
using Winche.Rules;
using Winche.Rules.Evaluation;

namespace Winche.Database.Authorization;

/// <summary>
/// <see cref="IDocumentListener"/> decorator that re-authorizes <see cref="RuleOperation.Get"/> on
/// <paramref name="path"/> before every emission carrying a document, terminating the subscription
/// with <see cref="AccessDeniedException"/> on denial instead of forwarding the document body.
/// </summary>
/// <remarks>
/// <paramref name="claims"/> is captured once at construction and reused for every re-evaluation —
/// connection claims are fixed at the HTTP upgrade and never refresh mid-connection, so there is no
/// fresher value to read. This means re-authorization does NOT catch token expiry or revocation;
/// that requires a connection-lifetime deadline and is out of scope here. What it DOES catch: the
/// current document contents, the current (possibly hot-swapped) ruleset, current <c>get()</c>/
/// <c>exists()</c> results, and a fresh <c>request.time</c>.
/// </remarks>
public sealed class RuleGuardedDocumentListener(
    IDocumentListener inner,
    RuleEngine engine,
    IDocumentDatabase core,
    string path,
    IReadOnlyDictionary<string, object?>? claims)
    : IDocumentListener
{
    private IAsyncEnumerator<DocumentSnapshot>? _enumerator;

    public async IAsyncEnumerable<DocumentSnapshot> Snapshots([EnumeratorCancellation] CancellationToken ct = default)
    {
        _enumerator ??= inner.Snapshots(ct).GetAsyncEnumerator(ct);
        while (await _enumerator.MoveNextAsync())
        {
            var snapshot = _enumerator.Current;

            // Keyed on the document alone, never on Current: a snapshot carrying a body is always
            // authorized. An absent document (the "current" marker, or a delete) carries no content,
            // and a resource-dependent rule evaluated against a null resource would deny — which
            // would tear the subscription down on every legitimate delete.
            if (snapshot.Document is not null)
                await AuthorizeAsync(snapshot, ct);

            yield return snapshot;
        }
    }

    private async Task AuthorizeAsync(DocumentSnapshot snapshot, CancellationToken ct)
    {
        var request = new RuleRequest
        {
            Resource = DocumentToResource.Convert(snapshot.Document!),
            Request = RequestBuilder.Build(claims, "get", RuleValue.Null),
            // Fresh per evaluation: reusing one provider across emissions would serve stale
            // get()/exists() results and defeat re-authorization entirely.
            Provider = new PostgresDocumentSource(core),
        };
        if (!await engine.AllowsAsync(RuleOperation.Get, path, request, ct))
            throw new AccessDeniedException(path, "get");
    }

    public async ValueTask DisposeAsync()
    {
        if (_enumerator is not null)
            await _enumerator.DisposeAsync();
        await inner.DisposeAsync();
    }
}
