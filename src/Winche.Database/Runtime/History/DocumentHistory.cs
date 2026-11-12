using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Values;

namespace Winche.Database.Runtime.History;

/// <summary>One field's change: <c>added</c>, <c>removed</c> or <c>changed</c>, with its values before and after.</summary>
public sealed record FieldChange(string Field, string Kind, Value? Before, Value? After);

/// <summary>
/// One change to a document. <see cref="Type"/> is <c>added</c>, <c>modified</c> or <c>removed</c>; <see cref="Actor"/>
/// who made it (null: unknown, e.g. a hook); <see cref="Before"/> / <see cref="After"/> the document around it, and
/// <see cref="Fields"/> what changed — null when the change predates <see cref="DependencyInjection.WincheDatabaseOptions.KeepHistory"/>.
/// </summary>
public sealed record HistoryEntry(
    long Seq, string Type, string Path, long Version, DateTimeOffset At, string? Actor,
    Document? Before, Document? After, IReadOnlyList<FieldChange>? Fields);

/// <summary>Which changes to read: one document, everything under a path, by an actor; newest first, paged by <see cref="Before"/>.</summary>
public sealed record HistoryQuery
{
    /// <summary>One document's path.</summary>
    public string? Path { get; init; }
    /// <summary>Everything under this path (a collection, or a document's subcollections), e.g. <c>users</c>.</summary>
    public string? Under { get; init; }
    public string? Actor { get; init; }
    /// <summary>Changes older than this <see cref="HistoryEntry.Seq"/>.</summary>
    public long? Before { get; init; }
    public int Limit { get; init; } = 50;
}

/// <summary>Reads document history (<see cref="DependencyInjection.WincheDatabaseOptions.KeepHistory"/>) from the change feed.</summary>
public sealed class DocumentHistory([FromKeyedServices(WincheDatabaseKeys.DataSource)] NpgsqlDataSource source)
{
    public async Task<IReadOnlyList<HistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var where = new List<string>();
        await using var cmd = source.CreateCommand();
        if (query.Path is { Length: > 0 } path)
        {
            where.Add("document_path = @path");
            cmd.Parameters.AddWithValue("path", path.Trim('/'));
        }
        if (query.Under is { Length: > 0 } under)
        {
            // A byte-order range over the path: everything that starts with "<under>/".
            where.Add("document_path COLLATE \"C\" >= @lo AND document_path COLLATE \"C\" < @hi");
            var prefix = under.Trim('/') + "/";
            cmd.Parameters.AddWithValue("lo", prefix);
            cmd.Parameters.AddWithValue("hi", prefix[..^1] + "0");   // '/' + 1
        }
        if (query.Actor is { Length: > 0 } actor)
        {
            where.Add("actor = @actor");
            cmd.Parameters.AddWithValue("actor", actor);
        }
        if (query.Before is { } beforeSeq)
        {
            where.Add("seq < @before");
            cmd.Parameters.AddWithValue("before", beforeSeq);
        }
        cmd.Parameters.AddWithValue("limit", Math.Clamp(query.Limit, 1, 500));
        cmd.CommandText = $"""
            SELECT seq, type, document_path, version, commit_time, actor, before::text, after::text
            FROM {WincheDatabaseTables.Changes}
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY seq DESC LIMIT @limit
            """;

        var list = new List<HistoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var docPath = reader.GetString(2);
            var type = reader.GetString(1);
            var before = reader.IsDBNull(6) ? null : ChangeSnapshots.Decode(docPath, reader.GetString(6));
            var after = reader.IsDBNull(7) ? null : ChangeSnapshots.Decode(docPath, reader.GetString(7));
            var recorded = type switch
            {
                "added" => after is not null,
                "removed" => before is not null,
                _ => before is not null && after is not null,
            };
            list.Add(new HistoryEntry(
                reader.GetInt64(0), type, docPath, reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), before, after,
                recorded ? Diff(before?.Fields, after?.Fields) : null));
        }
        return list;
    }

    /// <summary>The top-level fields that differ, in name order.</summary>
    public static IReadOnlyList<FieldChange> Diff(IReadOnlyDictionary<string, Value>? before, IReadOnlyDictionary<string, Value>? after)
    {
        before ??= new Dictionary<string, Value>();
        after ??= new Dictionary<string, Value>();
        var changes = new List<FieldChange>();
        foreach (var name in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var had = before.TryGetValue(name, out var b);
            var has = after.TryGetValue(name, out var a);
            if (had && has)
            {
                if (!b!.Equals(a)) changes.Add(new FieldChange(name, "changed", b, a));
            }
            else if (has) changes.Add(new FieldChange(name, "added", null, a));
            else changes.Add(new FieldChange(name, "removed", b, null));
        }
        return changes;
    }
}
