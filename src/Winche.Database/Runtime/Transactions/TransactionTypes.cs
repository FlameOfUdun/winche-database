namespace Winche.Database.Runtime.Transactions;

public sealed record TransactionHandle(string Id);

/// <summary>MaxAttempts for RunTransactionAsync (default: 5).</summary>
public sealed record TransactionOptions(int MaxAttempts = 5);

/// <summary>ABORTED: read-set conflict, expired/unknown transaction, or write contention. Retryable.</summary>
public sealed class TransactionAbortedException(string message)
    : RuntimeException(RuntimeStatus.Aborted, message);
