using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Sentinel.Models;

namespace Winche.Database.Sample.Configurations;

/// <summary>
/// Explicit aggregate opt-in for the "smoke-users" collection. Aggregation is gated by
/// <see cref="AccessOperation.Aggregate"/>, which is deliberately separate from Read: an aggregate
/// result can reveal information about documents the caller cannot read per-document, so read access
/// must not imply aggregate access. Collections without such a rule cannot be aggregated.
/// </summary>
internal class SmokeUsersAggregateRule : DocumentAccessRule
{
    public override string Path => "smoke-users";

    public override IReadOnlySet<AccessOperation> Operations =>
        new HashSet<AccessOperation> { AccessOperation.Aggregate };

    public override Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct) =>
        Task.FromResult(true);
}
