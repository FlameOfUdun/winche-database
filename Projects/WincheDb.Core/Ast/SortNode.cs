namespace WincheDb.Core.Ast
{
    public sealed record SortNode(string Field, SortDirection Direction = SortDirection.Asc, FieldType? Type = null);
}
