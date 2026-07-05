namespace Winche.Database.Constants;

/// <summary>DI service keys for Winche.Database's keyed service registrations.</summary>
public static class WincheDatabaseKeys
{
    /// <summary>
    /// Public key for this package's isolated rules engine and its rule-set repository.
    /// Resolve <c>[FromKeyedServices(WincheDatabaseKeys.RuleEngine)] RuleEngine</c> to evaluate,
    /// or <c>[FromKeyedServices(WincheDatabaseKeys.RuleEngine)] IMutableRuleSetRepository</c> to hot-swap.
    /// </summary>
    public const string RuleEngine = "WincheDatabase.RuleEngine";

    /// <summary>
    /// The store's <c>NpgsqlDataSource</c> key. Internal — an implementation detail, not a consumer surface.
    /// </summary>
    internal const string DataSource = "WincheDatabase";
}
