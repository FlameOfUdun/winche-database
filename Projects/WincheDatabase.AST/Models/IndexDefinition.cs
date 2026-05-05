namespace WincheDatabase.AST.Models;

public abstract class IndexDefinition
{
    public abstract string Collection { get; }
    public abstract List<SortNode> Fields { get; }
    public virtual string? Name { get; } = null;
    public virtual WhereNode? Where { get; } = null;
}
