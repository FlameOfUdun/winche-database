using System.Threading.Channels;
using Winche.Database.Abstraction;
using Winche.Database.Models;

namespace Winche.Database.Interfaces;

public interface IHookInvocationDispatcher
{
    IEnumerable<(DocumentStoreHook Hook, ChannelReader<HookInvocation> Reader)> Readers { get; }
    void Enqueue(string path, Func<DocumentStoreHook, CancellationToken, Task> invoke);
    void Complete();
}
