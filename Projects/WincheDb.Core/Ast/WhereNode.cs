namespace WincheDb.Core.Ast
{
    public abstract record WhereNode;
    public sealed record FieldFilter(string Field, ConditionalOperator Operator, object? Value) : WhereNode;
    public sealed record LogicGroup(LogicalOperator Operator, List<WhereNode> Children) : WhereNode;
    public sealed record FieldCompare(string Left, ConditionalOperator Operator, string Right, FieldType? Type = null) : WhereNode;
}
