namespace WincheDb.Core.Ast
{
    public record Query
    {
        public required string Collection { get; set; }
        public WhereNode? Where { get; set; }
        public List<SortNode> OrderBy { get; set; } = [];
        public int Limit { get; set; } = 100;
        public List<object?> StartAfter { get; set; } = [];
        public List<object?> StartAt { get; set; } = [];
        public List<object?> EndBefore { get; set; } = [];
        public List<object?> EndAt { get; set; } = [];
        public List<IncludeQuery> Include { get; set; } = [];
    }

    public sealed record IncludeQuery : Query
    {
        public required string Field { get; set; }
    }
}
