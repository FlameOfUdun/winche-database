using System.Threading.Channels;
using WincheDatabase.Store.Abstraction;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Interfaces;

public interface IHookInvocationDispatcher
{
    IEnumerable<(DocumentStoreHook Hook, ChannelReader<HookInvocation> Reader)> Readers { get; }
    void Enqueue(string path, Func<DocumentStoreHook, CancellationToken, Task> invoke);
    void Complete();
}
