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

    /// <summary>
    /// Key for the privileged, rule-free <see cref="Runtime.DocumentDatabase"/> core — it enforces
    /// NO read or write authorization (see the "null <c>IWriteAuthorizer</c>" warning on
    /// <see cref="Runtime.DocumentDatabase"/>'s XML doc). Registered under this key specifically so
    /// that <c>provider.GetService&lt;DocumentDatabase&gt;()</c> (the plain, unkeyed resolution)
    /// returns <see langword="null"/> — a consumer must deliberately opt in with
    /// <c>[FromKeyedServices(WincheDatabaseKeys.PrivilegedCore)]</c> to get an access-rule bypass.
    /// Reserved for the hosted sweepers and other trusted internal server-side code.
    /// </summary>
    public const string PrivilegedCore = "WincheDatabase.PrivilegedCore";
}
