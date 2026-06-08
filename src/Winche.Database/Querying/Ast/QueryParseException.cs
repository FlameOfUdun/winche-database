namespace Winche.Database.Querying.Ast;

/// <summary>Wire JSON could not be parsed into a Query. JsonPath points at the offending token.</summary>
public sealed class QueryParseException(string message, string jsonPath)
    : Exception($"{message} (at {jsonPath})")
{
    public string JsonPath { get; } = jsonPath;
}
