using Winche.Database.Abstraction;
using Winche.Database.Core.Models;
using Winche.Sentinel.Models;

namespace Winche.Database.Sample.Configurations;

internal class SmokeUsersCollectionReadRule : DocumentAccessRule
{
    public override string Path => "smoke-users";

    public override IReadOnlySet<AccessOperation> Operations =>
        new HashSet<AccessOperation> { AccessOperation.Read };

    public override Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct) =>
        Task.FromResult(true);
}
