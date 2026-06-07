namespace Winche.Database.Querying.Planning;

/// <summary>A structurally valid AST that violates a query rule. Code is a stable machine-readable rule id.</summary>
public sealed class PlanValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
