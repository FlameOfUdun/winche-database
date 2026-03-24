namespace WincheDb.DocumentStore.Models
{
    public record QuerySubscription
    {
        public string Id { get; init; } = string.Empty;
        public QueryResult Result { get; init; } = new();
    }
}
