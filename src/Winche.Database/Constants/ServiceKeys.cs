namespace Winche.Database.Constants;

/// <summary>
/// Contains constant keys used for service registration in the Winche Database system.
/// </summary>
internal sealed class ServiceKeys
{
    /// <summary>
    /// The keyed-service key under which the store's NpgsqlDataSource is registered.
    /// </summary>
    public const string DATA_SOURCE_KEY = "WincheDatabase";

    /// <summary>
    /// The keyed-service key under which this package's isolated <c>RuleEngine</c> is registered.
    /// Distinct from any other package's engine so rulesets never merge.
    /// </summary>
    public const string RULE_ENGINE_KEY = "WincheDatabase.RuleEngine";
}
