using Winche.Database.Documents;

namespace Winche.Database.Tests.Documents;

public class CollectionPageTokenTests
{
    [Fact]
    public void Encode_Then_Decode_RoundTrips()
    {
        var token = CollectionPageToken.Encode("orders");
        Assert.NotEqual("orders", token);              // opaque, not the raw id
        Assert.Equal("orders", CollectionPageToken.Decode(token));
    }

    [Fact]
    public void Decode_InvalidBase64_Throws()
    {
        Assert.Throws<ArgumentException>(() => CollectionPageToken.Decode("not base64!!!"));
    }

    [Fact]
    public void Decode_NullOrEmpty_Throws()
    {
        Assert.Throws<ArgumentException>(() => CollectionPageToken.Decode(""));
        Assert.Throws<ArgumentException>(() => CollectionPageToken.Decode(null!));
    }
}
