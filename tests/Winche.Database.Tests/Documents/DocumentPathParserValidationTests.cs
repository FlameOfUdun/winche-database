using Winche.Database.Documents;
using Xunit;

namespace Winche.Database.Tests.Documents;

/// <summary>
/// Guards path-shape validation: <see cref="DocumentPathParser"/> must validate SEGMENTS, not
/// slash counts, so a non-canonical path (leading/trailing/doubled '/') is rejected outright
/// rather than silently normalized to a canonical one behind an attacker's back.
/// </summary>
public class DocumentPathParserValidationTests
{
    [Theory]
    [InlineData("//users/mallory")]     // leading '/' → empty first segment
    [InlineData("users//mallory")]      // doubled '/' → empty segment
    [InlineData("/users/mallory")]      // leading '/'
    [InlineData("users/mallory/")]      // trailing '/'
    [InlineData("users/./mallory")]     // '.' segment
    [InlineData("users/../mallory")]    // '..' segment
    [InlineData("users/__name__")]      // reserved segment
    [InlineData("__x__/mallory")]       // reserved segment
    [InlineData("")]                    // empty
    public void IsValidDocumentPath_RejectsNonCanonicalPaths(string path) =>
        Assert.False(DocumentPathParser.IsValidDocumentPath(path, out _));

    [Fact]
    public void IsValidDocumentPath_RejectsSegmentOver1500Utf8Bytes()
    {
        var tooLong = new string('a', 1501);
        Assert.False(DocumentPathParser.IsValidDocumentPath($"users/{tooLong}", out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("users/mallory")]
    [InlineData("users/mallory/posts/p1")]
    [InlineData("a/b/c/d/e/f/g/h")]
    [InlineData("users/a-b_c")]
    [InlineData("users/a.b")]           // dot allowed when not the whole segment
    [InlineData("users/café")]          // non-ASCII segment
    public void IsValidDocumentPath_AcceptsCanonicalPaths(string path) =>
        Assert.True(DocumentPathParser.IsValidDocumentPath(path, out var error));

    [Fact]
    public void IsValidDocumentPath_AcceptsSegmentAtExactly1500Utf8Bytes()
    {
        var maxLen = new string('a', 1500);
        Assert.True(DocumentPathParser.IsValidDocumentPath($"users/{maxLen}", out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("//users/mallory")]
    [InlineData("users//mallory")]
    [InlineData("/users")]
    [InlineData("users/")]
    [InlineData("users/./posts")]
    [InlineData("users/../posts")]
    [InlineData("users/__name__/posts")]
    [InlineData("__x__/mallory/posts")]
    [InlineData("")]
    public void IsValidCollectionPath_RejectsNonCanonicalPaths(string path) =>
        Assert.False(DocumentPathParser.IsValidCollectionPath(path, out _));

    [Theory]
    [InlineData("users")]
    [InlineData("users/mallory/posts")]
    [InlineData("a/b/c/d/e")]
    public void IsValidCollectionPath_AcceptsCanonicalPaths(string path) =>
        Assert.True(DocumentPathParser.IsValidCollectionPath(path, out var error));

    // ── shape parity: a document path and a collection path are mutually exclusive shapes ──

    [Theory]
    [InlineData("users")]
    [InlineData("users/mallory/posts")]
    public void CollectionShapedPaths_AreInvalidAsDocumentPaths(string collectionPath) =>
        Assert.False(DocumentPathParser.IsValidDocumentPath(collectionPath, out _));

    [Theory]
    [InlineData("users/mallory")]
    [InlineData("users/mallory/posts/p1")]
    public void DocumentShapedPaths_AreInvalidAsCollectionPaths(string documentPath) =>
        Assert.False(DocumentPathParser.IsValidCollectionPath(documentPath, out _));

    // ── IsValidPath: either shape, used by cascade deletes ──

    [Theory]
    [InlineData("//users/mallory")]
    [InlineData("users//mallory")]
    [InlineData("/users/mallory")]
    [InlineData("users/mallory/")]
    [InlineData("users/./mallory")]
    [InlineData("users/../mallory")]
    [InlineData("users/__name__")]
    [InlineData("__x__/mallory")]
    [InlineData("")]
    public void IsValidPath_RejectsNonCanonicalPaths(string path) =>
        Assert.False(DocumentPathParser.IsValidPath(path, out _));

    [Theory]
    [InlineData("users")]                    // collection shape
    [InlineData("users/mallory")]             // document shape
    [InlineData("users/mallory/posts")]
    public void IsValidPath_AcceptsEitherCanonicalShape(string path) =>
        Assert.True(DocumentPathParser.IsValidPath(path, out var error));
}
