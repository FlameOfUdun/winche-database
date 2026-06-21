using System.Text;

namespace Winche.Database.Documents;

/// <summary>
/// Opaque pagination cursor for ListCollectionIds. The token is the base64 of the
/// last returned collection id; callers must treat it as opaque.
/// </summary>
public static class CollectionPageToken
{
    public static string Encode(string collectionId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(collectionId));

    public static string Decode(string token)
    {
        if (string.IsNullOrEmpty(token))
            throw new ArgumentException("Page token cannot be null or empty.", nameof(token));
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch (FormatException)
        {
            throw new ArgumentException($"Invalid page token '{token}'.", nameof(token));
        }
    }
}
