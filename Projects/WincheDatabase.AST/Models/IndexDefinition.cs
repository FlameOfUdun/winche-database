namespace WincheDatabase.AST.Models;

public sealed class IndexDefinition(string collection, List<SortNode> fields)
{
    public string Collection { get; init; } = collection;
    public List<SortNode> Fields { get; init; } = fields;
    public string? Name { get; init; }
    public WhereNode? Where { get; init; }
}
