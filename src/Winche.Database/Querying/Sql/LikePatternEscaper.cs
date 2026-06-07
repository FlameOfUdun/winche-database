namespace Winche.Database.Querying.Sql;

internal static class LikePatternEscaper
{
    /// <summary>Escapes \, % and _ so user paths can't act as LIKE wildcards (ESCAPE '\').</summary>
    internal static string Escape(string input) =>
        input.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
