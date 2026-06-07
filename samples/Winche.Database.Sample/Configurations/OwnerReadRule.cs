using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Sentinel.Models;

namespace Winche.Database.Sample.Configurations;

internal class OwnerReadRule : DocumentAccessRule
{
    public override string Path => "smoke-users/{userId}";

    public override IReadOnlySet<AccessOperation> Operations =>
        new HashSet<AccessOperation> { AccessOperation.Read };

    public override Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct)
    {
        var uid = context.Claims.TryGetValue("uid", out var v) ? v as string : null;
        context.Params.TryGetValue("userId", out var userId);
        return Task.FromResult(uid != null && uid == userId);
    }
}
