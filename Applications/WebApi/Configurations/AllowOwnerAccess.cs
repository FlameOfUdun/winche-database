

using System.Text.Json;
using WincheDatabase.Core.Models;
using WincheDatabase.Store.Models;
using WincheSentinel.Core.Models;

namespace WincheDatabase.Sample.Configurations;

internal class AllowPublicAccess : DocumentAccessRule
{
    public override string Path => "**";

    public override IReadOnlySet<AccessOperation> Operations => new HashSet<AccessOperation> { AccessOperation.Read, AccessOperation.Write, AccessOperation.Delete };

    public override async Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct)
    {
        return true;
    }
}