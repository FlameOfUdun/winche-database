using System.Security.Cryptography;

namespace Winche.Database.Documents;

/// <summary>
/// Auto-generated document id: 20 characters drawn uniformly from a 62-char base62 alphabet,
/// cryptographically random. Implements the 20-char auto-id scheme.
/// </summary>
public static class DocumentId
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int Length = 20;

    /// <summary>Returns a new 20-character base62, cryptographically random document identifier.</summary>
    public static string NewId()
    {
        // Rejection sampling to remove modulo bias: 248 = 4*62 is the largest multiple of 62 <= 255.
        const int ceiling = 256 - (256 % 62); // 248
        Span<char> chars = stackalloc char[Length];
        Span<byte> bytes = stackalloc byte[Length];
        var produced = 0;
        while (produced < Length)
        {
            RandomNumberGenerator.Fill(bytes);
            foreach (var b in bytes)
            {
                if (b >= ceiling) continue;            // drop the biased tail
                chars[produced++] = Alphabet[b % 62];
                if (produced == Length) break;
            }
        }
        return new string(chars);
    }
}
