namespace Winche.Database.Runtime;

/// <summary>Firestore-parity status codes for runtime operations.</summary>
public enum RuntimeStatus
{
    InvalidArgument,
    NotFound,
    AlreadyExists,
    FailedPrecondition,
    Aborted,
    DeadlineExceeded,
}

public class RuntimeException(RuntimeStatus status, string message) : Exception(message)
{
    public RuntimeStatus Status { get; } = status;
}
