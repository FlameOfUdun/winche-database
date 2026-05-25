namespace Winche.Database.Models;

public sealed record HookInvocation(Func<CancellationToken, Task> Invoke);
