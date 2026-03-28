using WincheDb.DocumentStore.Services;
using WincheDb.Realtime.Messages;

namespace WincheDb.Realtime.Handlers;

internal sealed class SyncPushHandler(
    TransactionManager transactionManager,
    DocumentManager documentManager
) : IMessageHandler<SyncPushRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, SyncPushRequest request, CancellationToken ct)
    {
        if (request.Mutations.Count == 0)
        {
            return new SyncPushResponse
            {
                RequestId = request.Id,
                Path = request.Path,
                Document = await documentManager.GetAsync(request.Path, ct),
                AppliedCount = 0,
                HasConflict = false,
            };
        }

        var tx = await transactionManager.BeginAsync(ct);
        var appliedCount = 0;

        try
        {
            var current = await tx.GetAsync(request.Path, ct);

            // Check base version on first mutation only — subsequent mutations
            // operate within the same transaction on the already-locked row.
            var firstMutation = request.Mutations[0];
            if (firstMutation.BaseVersion.HasValue
                && current != null
                && current.Version != firstMutation.BaseVersion.Value)
            {
                await tx.RollbackAsync(ct);
                await tx.DisposeAsync();

                return new SyncPushResponse
                {
                    RequestId = request.Id,
                    Path = request.Path,
                    Document = current,
                    AppliedCount = 0,
                    HasConflict = true,
                };
            }

            foreach (var mutation in request.Mutations)
            {
                switch (mutation.Type)
                {
                    case "set":
                        current = await tx.SetAsync(request.Path, mutation.Data!, ct);
                        break;

                    case "update":
                        current = await tx.UpdateAsync(request.Path, mutation.Data!, ct);
                        break;

                    case "delete":
                        await tx.DeleteAsync(request.Path, ct);
                        current = null;
                        break;

                    default:
                        await tx.RollbackAsync(ct);
                        await tx.DisposeAsync();
                        return new SystemErrorResponse
                        {
                            RequestId = request.Id,
                            Code = "invalid_mutation_type",
                            Message = $"Unknown mutation type: {mutation.Type}"
                        };
                }

                appliedCount++;
            }

            await tx.CommitAsync(ct);
            await tx.DisposeAsync();

            return new SyncPushResponse
            {
                RequestId = request.Id,
                Path = request.Path,
                Document = current,
                AppliedCount = appliedCount,
                HasConflict = false,
            };
        }
        catch
        {
            await tx.DisposeAsync();

            var serverDoc = await documentManager.GetAsync(request.Path, ct);
            return new SyncPushResponse
            {
                RequestId = request.Id,
                Path = request.Path,
                Document = serverDoc,
                AppliedCount = appliedCount,
                HasConflict = true,
            };
        }
    }
}
