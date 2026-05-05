using System.Threading.Channels;
using WincheDatabase.Core.Models;
using WincheDatabase.Store.Abstraction;
using WincheDatabase.Store.Interfaces;
using WincheDatabase.Store.Models;
using WincheSentinel.Core.Abstraction;

namespace WincheDatabase.Store.Services;

public sealed class HookInvocationDispatcher(
    IEnumerable<DocumentStoreHook> hooks,
    IPathPatternMatcher<Document> matcher
) : IHookInvocationDispatcher
{
    private readonly IReadOnlyDictionary<DocumentStoreHook, Channel<HookInvocation>> _channels =
        hooks.ToDictionary(h => h, _ => Channel.CreateUnbounded<HookInvocation>());

    public IEnumerable<(DocumentStoreHook Hook, ChannelReader<HookInvocation> Reader)> Readers =>
        _channels.Select(kv => (kv.Key, kv.Value.Reader));

    public void Enqueue(string path, Func<DocumentStoreHook, CancellationToken, Task> invoke)
    {
        foreach (var (hook, channel) in _channels)
        {
            var result = matcher.Match(hook.Path, path);
            if (!result.IsMatch) continue;
            channel.Writer.TryWrite(new HookInvocation(ct => invoke(hook, ct)));
        }
    }

    public void Complete()
    {
        foreach (var channel in _channels.Values)
            channel.Writer.Complete();
    }
}
