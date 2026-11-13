using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Values;

namespace Winche.Database.DependencyInjection;

/// <summary>Publishing document changes to the app's change sinks (Winche.Webhooks, Winche.Functions).</summary>
public static class DatabaseChangesExtensions
{
    /// <summary>The database's changes, as hooks and functions see them.</summary>
    public static readonly WincheChangeSource Source = new("database", "Documents", ["created", "updated", "deleted"], HasPaths: true,
        Variables: ["path", "id", "collection"]);

    /// <summary>
    /// Publishes every document change to the app's <see cref="IWincheChangeSink"/>s (Winche.Webhooks, and through it
    /// Winche.Functions' triggers), inside <c>AddWincheDatabase(o =&gt; o.UseHooks(h =&gt; h.PublishChanges()))</c>.
    /// Delivered at least once from the change feed. With <paramref name="withSnapshots"/> (default) changes carry the
    /// document before and after, at the cost of a copy per write in the feed.
    /// </summary>
    public static HookBuilder PublishChanges(this HookBuilder hooks, bool withSnapshots = true)
    {
        hooks.Add<ChangePublisher>("{document=**}", withSnapshots);
        hooks.Services.AddSingleton(Source);
        hooks.Services.TryAddSingleton(TimeProvider.System);
        return hooks;
    }

    /// <summary>The database's ready-made actions (<c>database.deleteDocuments</c>), registered with the database.</summary>
    internal static void AddDatabaseActions(IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWincheAction, DeleteDocumentsAction>());
}

/// <summary>Turns change-feed changes into <see cref="WincheChange"/>s for every sink.</summary>
internal sealed class ChangePublisher(IEnumerable<IWincheChangeSink> sinks, TimeProvider time) : DocumentStoreHook
{
    public override Task OnChangeAsync(DocumentChange change, CancellationToken ct)
    {
        var type = change.Type switch { ChangeType.Added => "created", ChangeType.Modified => "updated", _ => "deleted" };
        var segments = change.Path.Split('/');
        var vars = new Dictionary<string, string>
        {
            ["path"] = change.Path, ["id"] = segments[^1], ["collection"] = segments.Length > 1 ? segments[^2] : "",
        };
        return WincheChanges.PublishAsync(sinks,
            new WincheChange("database", type, change.Path, time.GetUtcNow(), Plain.Of(change.After), Plain.Of(change.Before), vars), ct);
    }
}

/// <summary>Ready-made: delete a document and everything under it (e.g. a user's data when their account goes).</summary>
internal sealed class DeleteDocumentsAction : IWincheAction
{
    public string Name => "database.deleteDocuments";
    public string Title => "Delete documents";
    public string Description => "Deletes a document and everything under it, in batches. Running it again is harmless.";
    public IReadOnlyList<string> Sources => [];
    public IReadOnlyList<WincheActionOption> Options { get; } =
        [new("path", "Document to delete (with everything under it)", "users/{uid}", Help: "{name} is filled from the trigger, e.g. {uid}.")];

    public async Task<string> RunAsync(WincheActionContext context, CancellationToken ct)
    {
        var path = context.Options["path"].Trim('/');
        var deleted = await context.Services.GetRequiredService<DocumentDatabase>().DeleteSubtreeAsync(path, ct: ct);
        return deleted == 0 ? $"Nothing at {path}." : $"Deleted {deleted} document{(deleted == 1 ? "" : "s")} at and under {path}.";
    }
}
