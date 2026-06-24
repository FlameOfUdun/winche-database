using Winche.Database.Documents;

namespace Winche.Database.Tests.Documents;

public class DocumentIdTests
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    [Fact]
    public void NewId_Is20Chars() => Assert.Equal(20, DocumentId.NewId().Length);

    [Fact]
    public void NewId_UsesBase62Alphabet()
    {
        for (var i = 0; i < 1000; i++)
            Assert.All(DocumentId.NewId(), ch => Assert.Contains(ch, Alphabet));
    }

    [Fact]
    public void NewId_IsUnique_OverManyDraws()
    {
        var ids = new HashSet<string>();
        for (var i = 0; i < 10_000; i++)
            Assert.True(ids.Add(DocumentId.NewId()), "duplicate id generated");
    }
}
