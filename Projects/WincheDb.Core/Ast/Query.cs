namespace WincheDb.Core.Ast
{
    public sealed record Query
    {
        public required string Collection { get; set; }
        public WhereNode? Where { get; set; }
        public List<SortNode> OrderBy { get; set; } = [];
        public int Limit { get; set; } = 100;
        public List<object?> StartAfter { get; set; } = [];
        public List<object?> StartAt { get; set; } = [];
        public List<object?> EndBefore { get; set; } = [];
        public List<object?> EndAt { get; set; } = [];
    }
}
