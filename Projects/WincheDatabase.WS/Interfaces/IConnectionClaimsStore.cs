namespace WincheDatabase.WS.Interfaces;

public interface IConnectionClaimsStore
{
    void SetClaims(string connectionId, IReadOnlyDictionary<string, object?> claims);
    IReadOnlyDictionary<string, object?> GetClaims(string connectionId);
    void Remove(string connectionId);
}
