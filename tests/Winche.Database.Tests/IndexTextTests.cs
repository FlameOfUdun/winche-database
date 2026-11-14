using Winche.Database.Settings;
using Xunit;

namespace Winche.Database.Tests;

public class IndexTextTests
{
    private const string File = """
        // Sessions: by user, newest first; gone once they expire.
        collection sessions {
          index (userId, startedAt desc)
          index (status, `created-at` desc)
          expire when expiresAt
        }

        collection group notes {
          index (tags contains)
          index (author.id, at)
        }

        collection sessions {
          index (device)
        }
        """;

    [Fact]
    public void A_file_parses_into_indexes_and_expiry()
    {
        var (result, error) = IndexText.Parse(File);
        Assert.Null(error);
        Assert.Equal(5, result!.Indexes.Count);
        var first = result.Indexes[0];
        Assert.Equal(("sessions", false, "ordered"), (first.Collection, first.CollectionGroup, first.Kind));
        Assert.Equal(["userId asc", "startedAt desc"], first.Fields.Select(f => $"{f.Path} {f.Direction}"));
        Assert.Equal("`created-at`", result.Indexes[1].Fields[1].Path);
        Assert.Equal(("notes", true, "array", "tags"), (result.Indexes[2].Collection, result.Indexes[2].CollectionGroup, result.Indexes[2].Kind, result.Indexes[2].Fields[0].Path));
        Assert.Equal("author.id", result.Indexes[3].Fields[0].Path);
        Assert.Equal("device", result.Indexes[4].Fields[0].Path);   // a collection in two blocks: its indexes add up
        Assert.Equal([new TtlSettings { Collection = "sessions", Field = "expiresAt" }], result.Ttl);
    }

    [Fact]
    public void Formatting_gives_one_block_per_collection_and_reads_back_the_same()
    {
        var (result, _) = IndexText.Parse(File);
        var text = IndexText.Format(result!.Indexes, result.Ttl);
        Assert.Equal("""
            collection sessions {
              index (userId, startedAt desc)
              index (status, `created-at` desc)
              index (device)
              expire when expiresAt
            }

            collection group notes {
              index (tags contains)
              index (author.id, at)
            }

            """.ReplaceLineEndings("\n"), text);
        var (again, error) = IndexText.Parse(text);
        Assert.Null(error);
        Assert.Equal(result.Indexes.Count, again!.Indexes.Count);
        Assert.Equal("", IndexText.Format([], []));
    }

    [Theory]
    [InlineData("collection sessions {\n  index (a, b contains)\n}", 2, "one field alone")]
    [InlineData("collection group notes {\n  expire when at\n}", 2, "not a group")]
    [InlineData("collection s {\n  expire when a\n  expire when b\n}", 3, "one expiry per collection")]
    [InlineData("collection s {\n  index (a\n}", 3, "',' or ')'")]
    [InlineData("collection s {\n  sort (a)\n}", 2, "'index' or 'expire'")]
    [InlineData("collection s {\n  index (a)", 2, "'}' is missing")]
    [InlineData("collection users/abc/posts { }", 1, "not a path")]
    [InlineData("collection s {\n  index (a)\n  index (a)\n}", 1, "listed twice")]
    public void Mistakes_are_named_where_they_are(string text, int line, string message)
    {
        var (_, error) = IndexText.Parse(text);
        Assert.NotNull(error);
        Assert.Equal(line, error!.Line);
        Assert.Contains(message, error.Message);
    }
}
