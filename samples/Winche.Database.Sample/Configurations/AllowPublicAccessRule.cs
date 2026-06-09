using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Sentinel.Models;

namespace Winche.Database.Sample.Configurations;

// Grants public Write/Delete on every path. It deliberately does NOT grant Read: access rules use
// OR semantics (any matching grant allows, default-deny otherwise), so a blanket "** Read" grant
// would make per-document read rules like OwnerReadRule unable to restrict anything. Reads are
// therefore granted narrowly by owner-scoped rules instead.
internal class AllowPublicAccessRule : DocumentAccessRule
{
    public override string Path => "**";

    public override IReadOnlySet<AccessOperation> Operations => new HashSet<AccessOperation> { AccessOperation.Write, AccessOperation.Delete };

    public override async Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct)
    {
        return true;
    }
}
