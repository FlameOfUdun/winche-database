namespace Winche.Database.Models
{
    public sealed record StoreOptions
    {
        public string Schema { get; set; } = "public";
        public string TableName { get; set; } = "documents";
        public TransactionConfig TransactionConfig { get; set; } = new();
        public ChangeFeedConfig ChangeFeed { get; set; } = new();
    }

    public sealed record ChangeFeedConfig
    {
        /// <summary>Feed rows older than this are pruned (spec §4; default 7 days).</summary>
        public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
        public TimeSpan PruneInterval { get; init; } = TimeSpan.FromMinutes(10);
        /// <summary>Poll fallback for missed notifies.</summary>
        public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
        public int BatchSize { get; init; } = 500;
    }

    public sealed record TransactionConfig
    {
        public TimeSpan TotalTimeoutSpan { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>Spec (runtime §3): optimistic transactions idle out after 60 seconds by default.</summary>
        public TimeSpan IdleTimeoutSpan { get; init; } = TimeSpan.FromMinutes(1);
        public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromSeconds(1);
    }
}
