using WincheDatabase.Store.Abstraction;
using WincheDatabase.WS.Abstraction;
using WincheDatabase.WS.Messages;

namespace WincheDatabase.WS.Handlers;

public sealed class TransactionBeginHandler(
    ITransactionManager transactionManager,
    ITransactionConnectionMap transactionConnectionMap
) : IMessageHandler<TransactionBeginRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, TransactionBeginRequest request, CancellationToken ct)
    {
        var transaction = await transactionManager.BeginAsync(ct);
        transactionConnectionMap.Track(connectionId, transaction.Id);

        return new TransactionBeginResponse
        {
            RequestId = request.Id,
            TransactionId = transaction.Id,
        };
    }
}

public sealed class TransactionGetHandler(
    ITransactionRegistry transactionRegistry,
    ITransactionConnectionMap transactionConnectionMap
) : IMessageHandler<TransactionGetRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, TransactionGetRequest request, CancellationToken ct)
    {
        if (!transactionConnectionMap.TryGetOwner(request.TransactionId, out var owner) || owner != connectionId ||
            !transactionRegistry.TryGet(request.TransactionId, out var transaction) || transaction == null)
        {
            return new SystemErrorResponse
            {
                RequestId = request.Id,
                Code = "transaction_not_found",
                Message = $"Transaction with ID {request.TransactionId} not found"
            };
        }

        var snapshot = await transaction.GetAsync(request.Path, ct);

        return new TransactionGetResponse
        {
            RequestId = request.Id,
            TransactionId = request.TransactionId,
            Document = snapshot,
        };
    }
}

public sealed class TransactionSetHandler(
    ITransactionRegistry transactionRegistry,
    ITransactionConnectionMap transactionConnectionMap
) : IMessageHandler<TransactionSetRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, TransactionSetRequest request, CancellationToken ct)
    {
        if (!transactionConnectionMap.TryGetOwner(request.TransactionId, out var owner) || owner != connectionId ||
            !transactionRegistry.TryGet(request.TransactionId, out var transaction) || transaction == null)
        {
            return new SystemErrorResponse
            {
                RequestId = request.Id,
                Code = "transaction_not_found",
                Message = $"Transaction with ID {request.TransactionId} not found"
            };
        }

        var snapshot = await transaction.SetAsync(request.Path, request.Data, ct);

        return new TransactionSetResponse
        {
            RequestId = request.Id,
            TransactionId = request.TransactionId,
            Document = snapshot
        };
    }
}

public sealed class TransactionUpdateHandler(
    ITransactionRegistry transactionRegistry,
    ITransactionConnectionMap transactionConnectionMap
) : IMessageHandler<TransactionUpdateRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, TransactionUpdateRequest request, CancellationToken ct)
    {
        if (!transactionConnectionMap.TryGetOwner(request.TransactionId, out var owner) || owner != connectionId ||
            !transactionRegistry.TryGet(request.TransactionId, out var transaction) || transaction == null)
        {
            return new SystemErrorResponse
            {
                RequestId = request.Id,
                Code = "transaction_not_found",
                Message = $"Transaction with ID {request.TransactionId} not found"
            };
        }

        var snapshot = await transaction.UpdateAsync(request.Path, request.Data, ct);

        return new TransactionUpdateResponse
        {
            RequestId = request.Id,
            TransactionId = request.TransactionId,
            Document = snapshot
        };
    }
}

public sealed class TransactionDeleteHandler(
    ITransactionRegistry transactionRegistry,
    ITransactionConnectionMap transactionConnectionMap
) : IMessageHandler<TransactionDeleteRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, TransactionDeleteRequest request, CancellationToken ct)
    {
        if (!transactionConnectionMap.TryGetOwner(request.TransactionId, out var owner) || owner != connectionId ||
            !transactionRegistry.TryGet(request.TransactionId, out var transaction) || transaction == null)
        {
            return new SystemErrorResponse
            {
                RequestId = request.Id,
                Code = "transaction_not_found",
                Message = $"Transaction with ID {request.TransactionId} not found"
            };
        }

        await transaction.DeleteAsync(request.Path, ct);

        return new TransactionDeleteResponse
        {
            RequestId = request.Id,
            TransactionId = request.TransactionId
        };
    }
}

public sealed class TransactionQueryHandler(
    ITransactionRegistry transactionRegistry,
    ITransactionConnectionMap transactionConnectionMap
) : IMessageHandler<TransactionQueryRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, TransactionQueryRequest request, CancellationToken ct)
    {
        if (!transactionConnectionMap.TryGetOwner(request.TransactionId, out var owner) || owner != connectionId ||
            !transactionRegistry.TryGet(request.TransactionId, out var transaction) || transaction == null)
        {
            return new SystemErrorResponse
            {
                RequestId = request.Id,
                Code = "transaction_not_found",
                Message = $"Transaction with ID {request.TransactionId} not found"
            };
        }

        var result = await transaction.QueryAsync(request.Query, ct);

        return new TransactionQueryResponse
        {
            RequestId = request.Id,
            TransactionId = request.TransactionId,
            Result = result
        };
    }
}

public sealed class TransactionCommitHandler(
    ITransactionConnectionMap transactionConnectionMap,
    ITransactionRegistry transactionRegistry
) : IMessageHandler<TransactionCommitRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, TransactionCommitRequest request, CancellationToken ct)
    {
        if (!transactionConnectionMap.TryGetOwner(request.TransactionId, out var owner) || owner != connectionId ||
            !transactionRegistry.TryGet(request.TransactionId, out var transaction) || transaction == null)
        {
            return new SystemErrorResponse
            {
                RequestId = request.Id,
                Code = "transaction_not_found",
                Message = $"Transaction with ID {request.TransactionId} not found"
            };
        }

        await transaction.CommitAsync(ct);

        transactionConnectionMap.Untrack(connectionId, request.TransactionId);
        transactionRegistry.TryRemove(request.TransactionId, out _);

        await transaction.DisposeAsync();

        return new TransactionCommitResponse
        {
            RequestId = request.Id,
            TransactionId = request.TransactionId
        };
    }
}

public sealed class TransactionRollbackHandler(
    ITransactionConnectionMap transactionConnectionMap,
    ITransactionRegistry transactionRegistry
) : IMessageHandler<TransactionRollbackRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, TransactionRollbackRequest request, CancellationToken ct)
    {
        if (!transactionConnectionMap.TryGetOwner(request.TransactionId, out var owner) || owner != connectionId ||
            !transactionRegistry.TryGet(request.TransactionId, out var transaction) || transaction == null)
        {
            return new SystemErrorResponse
            {
                RequestId = request.Id,
                Code = "transaction_not_found",
                Message = $"Transaction with ID {request.TransactionId} not found"
            };
        }

        await transaction.RollbackAsync(ct);

        transactionConnectionMap.Untrack(connectionId, request.TransactionId);
        transactionRegistry.TryRemove(request.TransactionId, out _);

        await transaction.DisposeAsync();

        return new TransactionRollbackResponse
        {
            RequestId = request.Id,
            TransactionId = request.TransactionId,
            Reason = request.Reason ?? "Transaction rolled back by client"
        };
    }
}
