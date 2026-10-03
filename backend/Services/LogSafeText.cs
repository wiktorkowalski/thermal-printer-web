using System.Globalization;
using System.Text;

namespace ThermalPrinterWeb.Services;

internal static class LogSafeText
{
    internal const string Missing = "-";

    private const char Replacement = '?';

    // Caller text: no character may start a new log line, hide text or close the quotes the template puts around the value.
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Missing;

        var cleaned = new StringBuilder(Math.Min(value.Length, maxLength));
        Span<char> chars = stackalloc char[2];
        // Runes: a cut never splits a surrogate pair, and a lone surrogate becomes U+FFFD.
        foreach (var rune in value.AsSpan().Trim().EnumerateRunes())
        {
            if (cleaned.Length + rune.Utf16SequenceLength > maxLength)
                break;

            if (rune.Value == '"')
                cleaned.Append('\'');
            else if (IsUnsafe(rune))
                cleaned.Append(Replacement);
            else
                cleaned.Append(chars[..rune.EncodeToUtf16(chars)]);
        }

        return cleaned.ToString();
    }

    private static bool IsUnsafe(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.Control            // C0, DEL, C1: CR, LF, ESC
        or UnicodeCategory.Format          // bidi overrides, zero-width characters
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator
        or UnicodeCategory.PrivateUse
        or UnicodeCategory.OtherNotAssigned;
}
