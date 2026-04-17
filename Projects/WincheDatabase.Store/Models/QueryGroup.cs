using WincheDatabase.AST.Models;

namespace WincheDatabase.Store.Models;

public sealed class QueryGroup
{
    public required string Key { get; init; }
    public required string Collection { get; init; }
    public required Query Query { get; init; }
    public QuerySnapshot Snapshot = new();
}
