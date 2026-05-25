namespace Winche.Database.Models
{
    public sealed record StoreOptions
    {
        public string Schema { get; set; } = "public";
        public string TableName { get; set; } = "documents";
        public TransactionConfig TransactionConfig { get; set; } = new();
    }

    public sealed record TransactionConfig
    {
        public TimeSpan TotalTimeoutSpan { get; init; } = TimeSpan.FromMinutes(5);
        public TimeSpan IdleTimeoutSpan { get; init; } = TimeSpan.FromMinutes(5);
        public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromSeconds(1);
    }
}
