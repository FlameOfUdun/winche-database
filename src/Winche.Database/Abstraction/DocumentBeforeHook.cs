using Winche.Database.Documents;
using Winche.Database.Runtime.Writes;
using Winche.Values;

namespace Winche.Database.Abstraction;

/// <summary>What a write does to one document.</summary>
public enum WriteOperation { Create, Update, Delete }

/// <summary>
/// Runs before a write is saved, inside its transaction, after the access rules allowed it: it can refuse the write
/// (throw <see cref="WincheDeniedException"/>, or call <see cref="DocumentWrite.Deny"/>) or change the document being
/// written (<see cref="DocumentWrite.Set"/>, <see cref="DocumentWrite.Remove"/>). It runs for every write, the app's
/// own server-side ones too. Registered per path with <c>o.UseHooks(h => h.Before&lt;THook&gt;("posts/{id}"))</c>.
/// Keep it quick: every matching write waits for it. Side effects (emails, other services) belong in a
/// <see cref="DocumentStoreHook"/>, which runs after the commit.
/// </summary>
public abstract class DocumentBeforeHook
{
    public abstract Task OnBeforeWriteAsync(DocumentWrite write, CancellationToken ct);
}

/// <summary>
/// One document a write is about to change, as a <see cref="DocumentBeforeHook"/> sees it. A batch, transaction or
/// cascading delete shows each document it changes once, with its final state; refusing any one refuses them all.
/// </summary>
public sealed class DocumentWrite
{
    private readonly ITransactionalDocumentReader _reader;
    private IReadOnlyDictionary<string, Value>? _fields;

    internal DocumentWrite(string path, WriteOperation operation, Document? before, IReadOnlyDictionary<string, Value>? fields,
        IReadOnlyDictionary<string, object?>? caller, ITransactionalDocumentReader reader)
    {
        Path = path;
        Operation = operation;
        Before = before;
        _fields = fields;
        Caller = caller;
        _reader = reader;
    }

    /// <summary>The document's path.</summary>
    public string Path { get; }

    /// <summary>Create, update or delete.</summary>
    public WriteOperation Operation { get; }

    /// <summary>The document as stored now; null for a create.</summary>
    public Document? Before { get; }

    /// <summary>The fields it will have, with any earlier hook's changes; null for a delete.</summary>
    public IReadOnlyDictionary<string, Value>? Fields => _fields;

    /// <summary>The claims of whoever made the request the write belongs to (<c>uid</c>, …); null when signed out or outside a request.</summary>
    public IReadOnlyDictionary<string, object?>? Caller { get; }

    /// <summary>The caller's uid, when signed in.</summary>
    public string? Uid => Caller?.GetValueOrDefault("uid") as string;

    /// <summary>Whether a hook changed <see cref="Fields"/>.</summary>
    public bool Changed { get; private set; }

    /// <summary>A field of the proposed document by dotted path (<c>address.city</c>), or null.</summary>
    public Value? Get(string fieldPath) =>
        _fields is not null && FieldMutator.TryGet(_fields, FieldPath.Parse(fieldPath), out var v) ? v : null;

    /// <summary>Sets a field of the document being written (dotted path; the value as for writes: string, number, bool, list, map, Value…).</summary>
    public DocumentWrite Set(string fieldPath, object? value)
    {
        _fields = FieldMutator.Set(Writable(), FieldPath.Parse(fieldPath), Value.FromObject(value));
        Changed = true;
        return this;
    }

    /// <summary>Removes a field of the document being written.</summary>
    public DocumentWrite Remove(string fieldPath)
    {
        _fields = FieldMutator.Delete(Writable(), FieldPath.Parse(fieldPath));
        Changed = true;
        return this;
    }

    /// <summary>Refuses the write: the caller gets permissionDenied with <paramref name="message"/>, and nothing is saved.</summary>
    public void Deny(string message) => throw new WincheDeniedException(message);

    /// <summary>Another document as stored (this write's own changes aren't saved yet), read in the write's transaction.</summary>
    public Task<Document?> GetAsync(string path, CancellationToken ct = default) => _reader.GetAsync(path, ct);

    private IReadOnlyDictionary<string, Value> Writable() =>
        _fields ?? throw new InvalidOperationException($"'{Path}' is being deleted: a delete can be refused, not changed.");
}

/// <summary>A <see cref="DocumentBeforeHook"/> bound to a path pattern.</summary>
public sealed record BeforeHookRegistration(string Path, DocumentBeforeHook Hook);

/// <summary>The app's before hooks, run by the write path in registration order.</summary>
internal sealed class BeforeWriteHooks(IEnumerable<BeforeHookRegistration> registrations, Func<IReadOnlyDictionary<string, object?>?> caller)
{
    private readonly BeforeHookRegistration[] _all = [.. registrations];

    public bool Any => _all.Length > 0;

    public bool Matches(string path) => _all.Any(r => Winche.Rules.Matching.PathMatcher.IsMatch(r.Path, path));

    public IReadOnlyDictionary<string, object?>? Caller() => caller();

    public async Task RunAsync(DocumentWrite write, CancellationToken ct)
    {
        foreach (var r in _all)
            if (Winche.Rules.Matching.PathMatcher.IsMatch(r.Path, write.Path))
                await r.Hook.OnBeforeWriteAsync(write, ct);
    }
}
