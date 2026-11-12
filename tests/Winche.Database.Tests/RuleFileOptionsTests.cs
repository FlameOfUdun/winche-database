using Winche.Database.DependencyInjection;
using Xunit;

namespace Winche.Database.Tests;

public class RuleFileOptionsTests
{
    private const string Valid = """
        match users/{uid} {
          allow read: if request.auth.uid == uid;
        }
        """;

    [Fact]
    public void UseRulesText_AddsParsedRuleSet()
    {
        var options = new WincheDatabaseOptions().UseRulesText(Valid);
        var block = Assert.Single(Assert.Single(options.Rulesets).Matches);
        Assert.Equal("users/{uid}", block.Path);
    }

    [Fact]
    public void UseRulesText_SyntaxError_ThrowsWithPosition()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new WincheDatabaseOptions().UseRulesText("match users/{uid} {\n  allow read: if ;\n}"));
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void UseRulesFile_ReadsRelativeToBaseDirectory()
    {
        var name = $"rules-{Guid.NewGuid():N}.rules";
        var full = Path.Combine(AppContext.BaseDirectory, name);
        File.WriteAllText(full, Valid);
        try
        {
            var options = new WincheDatabaseOptions().UseRulesFile(name);
            Assert.Single(options.Rulesets);
        }
        finally
        {
            File.Delete(full);
        }
    }

    [Fact]
    public void UseRulesFile_MissingFile_Throws() =>
        Assert.Throws<FileNotFoundException>(() => new WincheDatabaseOptions().UseRulesFile("nope.rules"));

    [Fact]
    public void UseRulesFile_BadFile_NamesTheFile()
    {
        var full = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.rules");
        File.WriteAllText(full, "match users/{uid} { allow read: if }");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new WincheDatabaseOptions().UseRulesFile(full));
            Assert.Contains(full, ex.Message);
        }
        finally
        {
            File.Delete(full);
        }
    }
}
