using Winche.Rules;
using Winche.Rules.Text;

namespace Winche.Database.DependencyInjection;

/// <summary>Loads rules-language text for <c>UseRulesText</c>/<c>UseRulesFile</c>, failing at startup.</summary>
internal static class RuleFiles
{
    public static RuleSet Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Rules file '{full}' was not found.", full);
        return Parse(File.ReadAllText(full), full);
    }

    public static RuleSet Parse(string text, string? source)
    {
        try
        {
            return RuleText.ParseRuleSet(text);
        }
        catch (RuleSyntaxException ex)
        {
            throw new InvalidOperationException(
                $"Invalid rules{(source is null ? "" : $" in '{source}'")} at line {ex.Line}, column {ex.Column}: {ex.Reason}", ex);
        }
        catch (ArgumentException ex)   // a structurally invalid match path
        {
            throw new InvalidOperationException($"Invalid rules{(source is null ? "" : $" in '{source}'")}: {ex.Message}", ex);
        }
    }
}
