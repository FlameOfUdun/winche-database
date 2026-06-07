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
}
