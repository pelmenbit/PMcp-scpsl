using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PMcp.Util;

public static class TextSanitizer
{
    private static readonly Regex Tags = new(@"<[^<>]{0,64}>");

    private static readonly Regex RichTag = new(@"<\s*(/?)\s*([a-zA-Z]+)([^<>]{0,64})>");
    private static readonly Regex ColorValue = new(@"^\s*=\s*""?(#[0-9a-fA-F]{3,8}|[a-zA-Z]+)""?\s*$");
    private static readonly string[] SafeTags = { "b", "i", "u", "s", "color" };

    public static string StripTags(string? value) => string.IsNullOrEmpty(value) ? "" : Tags.Replace(value, "");

    public static string SafeRichText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return RichTag.Replace(
            value,
            m =>
            {
                string name = m.Groups[2].Value.ToLowerInvariant();
                if (System.Array.IndexOf(SafeTags, name) < 0)
                    return "";
                string rest = m.Groups[3].Value;
                bool closing = m.Groups[1].Value.Length > 0;
                if (closing)
                    return rest.Trim().Length == 0 ? $"</{name}>" : "";
                if (name == "color")
                {
                    Match color = ColorValue.Match(rest);
                    return color.Success ? $"<color={color.Groups[1].Value}>" : "";
                }
                return rest.Trim().Length == 0 ? $"<{name}>" : "";
            }
        );
    }

    public static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        string stripped = Tags.Replace(value, "");
        var sb = new StringBuilder(stripped.Length < maxLength ? stripped.Length : maxLength);
        bool pendingSpace = false;
        bool truncated = false;

        foreach (char c in stripped)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
                continue;

            if (sb.Length + (pendingSpace ? 2 : 1) > maxLength)
            {
                truncated = true;
                break;
            }
            if (pendingSpace)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }

        if (truncated)
            sb.Append('…');
        return sb.ToString();
    }
}
