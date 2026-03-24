using WincheDb.DocumentStore.Abstraction;
using WincheDb.SqlBuilder;

namespace WincheDb.DocumentStore
{
    public sealed record StoreOptions
    {
        public string Schema { get; set; } = "public";
        public string TableName { get; set; } = "documents";
        public bool EnsureCreated { get; set; } = true;
        public List<IndexDefinition> Indexes { get; set; } = [];
        public TransactionConfig TransactionConfig { get; set; } = new();

        /// <summary>
        /// Optional access rule evaluated before every document and subscription operation.
        /// Return false to deny. Null means allow all.
        /// Mutually exclusive with <see cref="AccessRules"/>.
        /// </summary>
        public Func<AccessContext, CancellationToken, Task<bool>>? AccessRule { get; set; }

        /// <summary>
        /// Pattern-based access rules with wildcard path matching (Firestore-style).
        /// First matching rule wins. Deny by default when rules are defined but none match.
        /// Mutually exclusive with <see cref="AccessRule"/>.
        /// </summary>
        public List<AccessRule>? AccessRules { get; set; }
    }

    public sealed record TransactionConfig
    {
        public TimeSpan TotalTimeoutSpan { get; init; } = TimeSpan.FromMinutes(5);
        public TimeSpan IdleTimeoutSpan { get; init; } = TimeSpan.FromMinutes(5);
        public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromSeconds(1);
    }
}
