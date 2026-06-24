namespace Winche.Database.Runtime;

/// <summary>Status codes for runtime operations.</summary>
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
