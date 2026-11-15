using System.Text.Json.Nodes;

namespace Winche.Database.Sample.Configurations;

// Stand-ins for Winche.Storage and Winche Auth, which register the real "file" and "user" resolvers (with their rules,
// and live updates when a file or user changes). The sample has neither, so these answer from a fixed list: enough
// for the Flutter demo's file and user links to show data.

/// <summary>A few made-up files. Signed-in callers see them; nobody sees <c>private/…</c>; anything else is missing.</summary>
internal sealed class SampleFiles : IWincheReferenceResolver
{
    private static readonly Dictionary<string, JsonObject> Files = new(StringComparer.Ordinal)
    {
        ["scans/chest-xray.png"] = new() { ["path"] = "scans/chest-xray.png", ["mimeType"] = "image/png", ["sizeBytes"] = 482113, ["uploaded"] = true },
        ["scans/ecg.pdf"] = new() { ["path"] = "scans/ecg.pdf", ["mimeType"] = "application/pdf", ["sizeBytes"] = 91822, ["uploaded"] = true },
        ["private/notes.txt"] = new() { ["path"] = "private/notes.txt", ["mimeType"] = "text/plain", ["sizeBytes"] = 120, ["uploaded"] = true },
    };

    public string Kind => "file";
    public string? ChangeChannel => null;

    public Task<IReadOnlyDictionary<string, JsonObject>> ResolveAsync(IReadOnlyList<string> keys, ReferenceCaller caller, CancellationToken ct)
    {
        IReadOnlyDictionary<string, JsonObject> found = keys
            .Where(k => Files.ContainsKey(k) && (caller.Trusted || (caller.Uid is not null && !k.StartsWith("private/", StringComparison.Ordinal))))
            .ToDictionary(k => k, k => (JsonObject)Files[k].DeepClone());
        return Task.FromResult(found);
    }
}

/// <summary>The sample's two users, as their public profiles: to signed-in callers only.</summary>
internal sealed class SampleUsers : IWincheReferenceResolver
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal) { ["alice"] = "Alice Moreau", ["bob"] = "Bob Okafor" };

    public string Kind => "user";
    public string? ChangeChannel => null;

    public Task<IReadOnlyDictionary<string, JsonObject>> ResolveAsync(IReadOnlyList<string> keys, ReferenceCaller caller, CancellationToken ct)
    {
        IReadOnlyDictionary<string, JsonObject> found = caller.Uid is null && !caller.Trusted
            ? new Dictionary<string, JsonObject>()
            : keys.Where(Names.ContainsKey).ToDictionary(k => k, k => new JsonObject { ["uid"] = k, ["displayName"] = Names[k], ["photoUrl"] = null });
        return Task.FromResult(found);
    }
}
