using System.Text;

namespace Winche.Database.SQL.Infrastructure;

public static class LikePatternEscaper
{
    public static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '\\' || c == '%' || c == '_')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
