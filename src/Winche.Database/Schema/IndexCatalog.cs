using Winche.Database.Documents;
using Winche.Database.Runtime.Ttl;

namespace Winche.Database.Schema;

/// <summary>
/// The indexes and expiry (TTL) policies in force: the code's declarations, or the console's when the database uses
/// settings (<c>o.UseSettings()</c>). Query planning, index sync and the TTL sweeper read it, so a change applies
/// without a restart.
/// </summary>
public sealed class IndexCatalog
{
    private volatile State _state;

    private sealed record State(IReadOnlyList<IndexDefinition> Indexes, IReadOnlyList<TtlPolicy> Ttl);

    public IndexCatalog(IEnumerable<IndexDefinition> indexes, IEnumerable<TtlPolicy> ttl) => _state = new State([.. indexes], [.. ttl]);

    public IReadOnlyList<IndexDefinition> Indexes => _state.Indexes;
    public IReadOnlyList<TtlPolicy> TtlPolicies => _state.Ttl;

    /// <summary>Raised (on a background thread) after the indexes or policies change.</summary>
    public event Action? Changed;

    internal void Replace(IReadOnlyList<IndexDefinition> indexes, IReadOnlyList<TtlPolicy> ttl)
    {
        var before = _state;
        // Records holding lists compare those by reference: compare what defines them instead.
        static string IndexKey(IndexDefinition i) => Querying.Sql.IndexSql.NameOf(i);
        static string TtlKey(TtlPolicy t) => $"{t.CollectionId}|{t.Field}";
        if (before.Indexes.Select(IndexKey).SequenceEqual(indexes.Select(IndexKey)) && before.Ttl.Select(TtlKey).SequenceEqual(ttl.Select(TtlKey))) return;
        _state = new State(indexes, ttl);
        Changed?.Invoke();
    }
}
