using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Sentinel.Models;

namespace Winche.Database.Sample.Configurations;

internal class AllowPublicAccessRule : DocumentAccessRule
{
    public override string Path => "**";

    public override IReadOnlySet<AccessOperation> Operations => new HashSet<AccessOperation> { AccessOperation.Write, AccessOperation.Delete };

    public override async Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct)
    {
        return true;
    }
}
