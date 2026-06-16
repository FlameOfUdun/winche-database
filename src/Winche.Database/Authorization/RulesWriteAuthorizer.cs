using Winche.Database.Runtime.Writes;
using Winche.Rules;
using Winche.Rules.Evaluation;

namespace Winche.Database.Authorization;

/// <summary>
/// <see cref="IWriteAuthorizer"/> backed by the injected <see cref="RuleEngine"/>. Evaluates each write
/// inside the write transaction using the applier-computed post-write document as <c>request.resource</c>
/// and the real commit time as <c>request.time</c>. A single denied write throws, rolling back the batch.
/// </summary>
public sealed class RulesWriteAuthorizer(
    RuleEngine engine,
    IRuleClaimsAccessor claimsAccessor) : IWriteAuthorizer
{
    public async Task AuthorizeAsync(
        IReadOnlyList<PendingWrite> writes,
        ITransactionalDocumentReader reader,
        DateTimeOffset commitTime,
        CancellationToken ct)
    {
        var claims = claimsAccessor.GetClaims();
        var documents = new TransactionalRuleDocumentSource(reader);

        foreach (var pw in writes)
        {
            var (op, method) = pw.Write switch
            {
                DeleteWrite => (RuleOperation.Delete, "delete"),
                SetWrite when pw.Before is null => (RuleOperation.Create, "create"),
                SetWrite => (RuleOperation.Update, "update"),
                UpdateWrite => (RuleOperation.Update, "update"),
                _ => throw new NotSupportedException($"Unknown write type: {pw.Write.GetType().Name}"),
            };

            var resource = pw.Before is not null ? DocumentToResource.Convert(pw.Before) : RuleValue.Null;
            var requestResource = pw.After is not null ? DocumentToResource.Convert(pw.After) : RuleValue.Null;

            var request = new RuleRequest
            {
                Resource = resource,
                Request = RequestBuilder.Build(claims, method, requestResource, commitTime),
                Provider = documents,
            };

            if (!await engine.AllowsAsync(op, pw.Write.Path, request, ct))
                throw new AccessDeniedException(pw.Write.Path, method);
        }
    }
}
