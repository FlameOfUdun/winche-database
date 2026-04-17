namespace WincheDatabase.AST.Models
{
    public sealed record SortNode(string Field, SortDirection Direction = SortDirection.Asc, FieldType? Type = null);
}
