namespace WincheDatabase.Store.Models;

public sealed record HookInvocation(Func<CancellationToken, Task> Invoke);
