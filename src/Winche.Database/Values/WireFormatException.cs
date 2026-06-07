namespace Winche.Database.Values;

/// <summary>Thrown when wire JSON cannot be parsed into a Value.</summary>
public sealed class WireFormatException(string message) : Exception(message);
