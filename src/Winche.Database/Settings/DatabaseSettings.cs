using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Sql;
using Winche.Database.Runtime.Ttl;
using Winche.Database.Runtime.Writes;

namespace Winche.Database.Settings;

/// <summary>
/// The settings area <c>database</c> (<c>o.UseSettings()</c>): indexes, expiry, history, limits and request logging,
/// as the console changes them. The code's declarations are the starting values.
/// </summary>
public sealed record DatabaseSettings
{
    public const string Area = "database";

    public IReadOnlyList<IndexSettings> Indexes { get; init; } = [];
    /// <summary>Drop indexes this list no longer has (default true).</summary>
    public bool DropUndeclaredIndexes { get; init; } = true;
    /// <summary>Documents deleted once a timestamp field is in the past.</summary>
    public IReadOnlyList<TtlSettings> Ttl { get; init; } = [];
    public HistorySettings History { get; init; } = new();
    public WriteLimitSettings WriteLimits { get; init; } = new();
    /// <summary>Queries slower than this without an index are reported (and suggested in the console); null = off.</summary>
    public int? SlowQueryMs { get; init; } = 500;
    /// <summary>The most referenced documents, files and users one expand response includes.</summary>
    public int ExpandLimit { get; init; } = Runtime.Expansion.ExpandRequest.DefaultLimit;
    public RequestLogSettings RequestLog { get; init; } = new();

    public static DatabaseSettings From(WincheDatabaseOptions o, IEnumerable<IndexDefinition> indexes, IEnumerable<TtlPolicy> ttl) => new()
    {
        Indexes = [.. indexes.Select(IndexSettings.From)],
        DropUndeclaredIndexes = o.DropUndeclaredIndexes,
        Ttl = [.. ttl.Select(t => new TtlSettings { Collection = t.CollectionId, Field = t.Field.ToString() })],
        History = o.History is { } h ? new() { Enabled = true, RetentionDays = (int)h.Retention.TotalDays } : new(),
        WriteLimits = new() { MaxDocumentKb = (int)(o.WriteLimits.MaxDocumentSizeBytes / 1024), MaxDepth = o.WriteLimits.MaxDepth },
        SlowQueryMs = o.SlowQueryThreshold is { } t ? (int)t.TotalMilliseconds : null,
        ExpandLimit = o.ExpandLimit,
        RequestLog = o.RequestLog is { } r
            ? new() { Enabled = true, SampleRate = r.SuccessSampleRate, SlowMs = (int)r.SlowThreshold.TotalMilliseconds }
            : new(),
    };

    public IReadOnlyList<IndexDefinition> IndexDefinitions() => [.. Indexes.Select(i => i.ToDefinition())];
    public IReadOnlyList<TtlPolicy> TtlPolicies() => [.. Ttl.Select(t => TtlPolicy.For(t.Collection, t.Field))];

    /// <summary>Writes these settings onto the live options (indexes and TTL go to the catalog).</summary>
    internal void ApplyTo(WincheDatabaseOptions o)
    {
        o.DropUndeclaredIndexes = DropUndeclaredIndexes;
        o.History = History.Enabled ? new HistoryConfig { Retention = TimeSpan.FromDays(History.RetentionDays) } : null;
        o.WriteLimits = o.WriteLimits with { MaxDocumentSizeBytes = WriteLimits.MaxDocumentKb * 1024L, MaxDepth = WriteLimits.MaxDepth };
        o.SlowQueryThreshold = SlowQueryMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
        o.ExpandLimit = ExpandLimit;
        o.RequestLog = RequestLog.Enabled
            ? new Runtime.Logging.RequestLogOptions { SuccessSampleRate = RequestLog.SampleRate, SlowThreshold = TimeSpan.FromMilliseconds(RequestLog.SlowMs) }
            : null;
    }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in Indexes)
        {
            var label = $"Index on {i.Collection} ({string.Join(", ", i.Fields.Select(f => f.Path))})";
            if (i.Kind is not ("ordered" or "array")) { problems.Add($"{label}: the kind is ordered or array."); continue; }
            if (i.Fields.Any(f => f.Direction is not ("asc" or "desc"))) { problems.Add($"{label}: each field sorts asc or desc."); continue; }
            try
            {
                var d = i.ToDefinition();
                IndexSql.BuildCreate(d);
                if (!names.Add(IndexSql.NameOf(d))) problems.Add($"{label} is listed twice.");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidPathPatternException or FormatException)
            {
                problems.Add($"{label}: {ex.Message}");
            }
        }
        foreach (var t in Ttl)
        {
            if (!DocumentPathParser.IsValidCollectionId(t.Collection, out var error)) problems.Add($"Expiry on '{t.Collection}': {error}");
            try { FieldPath.Parse(t.Field); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { problems.Add($"Expiry on '{t.Collection}': the field '{t.Field}' isn't a field path."); }
        }
        if (Ttl.GroupBy(t => t.Collection).FirstOrDefault(g => g.Count() > 1) is { } twice) problems.Add($"'{twice.Key}' has two expiry fields: keep one.");
        if (History.Enabled && History.RetentionDays is < 1 or > 3650) problems.Add("Keep history 1 day to 10 years.");
        if (WriteLimits.MaxDocumentKb is < 1 or > 16 * 1024) problems.Add("Documents may be 1 KB to 16 MB.");
        if (WriteLimits.MaxDepth is < 1 or > 100) problems.Add("Documents may nest 1 to 100 levels.");
        if (ExpandLimit is < 1 or > Runtime.Expansion.ExpandRequest.MaxLimit) problems.Add($"Expand includes 1 to {Runtime.Expansion.ExpandRequest.MaxLimit:N0} references.");
        if (SlowQueryMs is < 1) problems.Add("The slow-query threshold must be at least 1 ms (or off).");
        if (RequestLog.Enabled && RequestLog.SampleRate is < 0 or > 1) problems.Add("Log between 0% and 100% of successful requests.");
        if (RequestLog.Enabled && RequestLog.SlowMs < 1) problems.Add("A slow request takes at least 1 ms.");
        return problems;
    }
}

public sealed record IndexSettings
{
    public string Collection { get; init; } = "";
    public IReadOnlyList<IndexFieldSettings> Fields { get; init; } = [];
    /// <summary>ordered (filters and sorting) or array (array-contains on one field).</summary>
    public string Kind { get; init; } = "ordered";
    /// <summary>Across every collection with this id, wherever it is (a collection group).</summary>
    public bool CollectionGroup { get; init; }

    public static IndexSettings From(IndexDefinition d) => new()
    {
        Collection = d.CollectionId,
        Fields = [.. d.Fields.Select(f => new IndexFieldSettings { Path = f.Path, Direction = f.Direction == SortDirection.Desc ? "desc" : "asc" })],
        Kind = d.Kind == IndexKind.ArrayMembership ? "array" : "ordered",
        CollectionGroup = d.CollectionGroup,
    };

    public IndexDefinition ToDefinition() => new(Collection,
        [.. Fields.Select(f => new IndexField(f.Path, f.Direction == "desc" ? SortDirection.Desc : SortDirection.Asc))],
        Kind: Kind == "array" ? IndexKind.ArrayMembership : IndexKind.Ordered, CollectionGroup: CollectionGroup);
}

public sealed record IndexFieldSettings
{
    public string Path { get; init; } = "";
    public string Direction { get; init; } = "asc";
}

public sealed record TtlSettings
{
    public string Collection { get; init; } = "";
    /// <summary>A timestamp field: the document is deleted once it's in the past.</summary>
    public string Field { get; init; } = "";
}

public sealed record HistorySettings
{
    public bool Enabled { get; init; }
    public int RetentionDays { get; init; } = 30;
}

public sealed record WriteLimitSettings
{
    public int MaxDocumentKb { get; init; } = 1024;
    public int MaxDepth { get; init; } = 20;
}

public sealed record RequestLogSettings
{
    public bool Enabled { get; init; }
    /// <summary>Share of successful requests logged (0–1); denied and slow ones always are.</summary>
    public double SampleRate { get; init; } = 0.1;
    public int SlowMs { get; init; } = 1000;
}
