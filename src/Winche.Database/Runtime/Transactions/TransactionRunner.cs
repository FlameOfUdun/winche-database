namespace Winche.Database.Runtime.Transactions;

/// <summary>The RunTransactionAsync retry loop: begin → body → commit; retry whole body on ABORTED.</summary>
internal static class TransactionRunner
{
    internal static async Task<T> RunAsync<T>(
        IDocumentDatabase db,
        Func<TransactionContext, Task<T>> body,
        TransactionOptions? options,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, (options ?? new TransactionOptions()).MaxAttempts);
        RuntimeException? lastAborted = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var handle = await db.BeginTransactionAsync(ct);
            var context = new TransactionContext(db, handle.Id);

            try
            {
                var result = await body(context);
                await db.CommitTransactionAsync(handle.Id, context.BufferedWrites, ct);
                return result;
            }
            catch (RuntimeException ex) when (ex.Status == RuntimeStatus.Aborted)
            {
                lastAborted = ex;
                await db.RollbackTransactionAsync(handle.Id, CancellationToken.None);   // idempotent
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt + Random.Shared.Next(0, 50)), ct);
            }
            catch
            {
                await db.RollbackTransactionAsync(handle.Id, CancellationToken.None);
                throw;
            }
        }

        throw lastAborted!;
    }
}
