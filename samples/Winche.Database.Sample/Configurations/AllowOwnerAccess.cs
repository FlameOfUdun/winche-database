using Winche.Database.Core.Models;
using Winche.Database.Models;
using WincheSentinel.Models;

namespace Winche.Database.Sample.Configurations;

internal class AllowPublicAccess : DocumentAccessRule
{
    public override string Path => "**";

    public override IReadOnlySet<AccessOperation> Operations => new HashSet<AccessOperation> { AccessOperation.Read, AccessOperation.Write, AccessOperation.Delete };

    public override async Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct)
    {
        return true;
    }
}