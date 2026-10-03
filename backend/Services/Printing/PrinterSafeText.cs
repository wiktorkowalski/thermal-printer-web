using System.Buffers;
using System.Text;

namespace ThermalPrinterWeb.Services.Printing;

// A control byte in caller text is an ESC/POS command; DLE commands act even
// inside another command's data. Sources: raw C0 input and best-fit encoding
// (PC852 maps U+2190 to 0x1B).
internal static class PrinterSafeText
{
    private const byte LineFeed = 0x0A;
    private const byte Replacement = (byte)'?';

    // Largest encoding in CodePages is UTF-8: 4 bytes per rune.
    private const int MaxBytesPerRune = 8;

    private static readonly byte[] C0AndDelete = [.. Enumerable.Range(0, 0x20).Select(b => (byte)b), 0x7F];
    private static readonly SearchValues<byte> ControlBytes = SearchValues.Create(C0AndDelete);
    private static readonly SearchValues<byte> ControlBytesExceptLineFeed =
        SearchValues.Create([.. C0AndDelete.Where(b => b != LineFeed)]);

    // Readable stand-ins, used only when the character would reach the printer
    // as a control byte under the active code page.
    private static readonly Dictionary<int, byte[]> Substitutes = new()
    {
        ['\t'] = " "u8.ToArray(),
        [0x00B7] = "."u8.ToArray(),   // middle dot
        [0x2022] = "*"u8.ToArray(),   // bullet
        [0x2024] = "."u8.ToArray(),   // one dot leader
        [0x2026] = "..."u8.ToArray(), // ellipsis
        [0x2190] = "<-"u8.ToArray(),
        [0x2191] = "^"u8.ToArray(),
        [0x2192] = "->"u8.ToArray(),
        [0x2193] = "v"u8.ToArray(),
        [0x2194] = "<->"u8.ToArray(),
        [0x2219] = "."u8.ToArray(),   // bullet operator
        [0x22C5] = "."u8.ToArray(),   // dot operator
        [0x25BA] = ">"u8.ToArray(),
        [0x25C4] = "<"u8.ToArray(),
        [0x30FB] = "."u8.ToArray(),   // katakana middle dot
    };

    // Encodes text for a Text or Separator block. The result has no byte below
    // 0x20 except LF, and no DEL. `replaced` counts characters that became '?'.
    public static byte[] Encode(string text, Encoding encoding, out int replaced)
    {
        replaced = 0;
        var normalized = text.ReplaceLineEndings("\n");
        var bytes = encoding.GetBytes(normalized);
        if (!bytes.AsSpan().ContainsAny(ControlBytesExceptLineFeed))
            return bytes;

        var output = new List<byte>(bytes.Length);
        Span<char> chars = stackalloc char[2];
        Span<byte> buffer = stackalloc byte[MaxBytesPerRune];
        foreach (var rune in normalized.EnumerateRunes())
        {
            var charCount = rune.EncodeToUtf16(chars);
            var encoded = buffer[..encoding.GetBytes(chars[..charCount], buffer)];

            if (rune.Value == LineFeed || !encoded.ContainsAny(ControlBytes))
                output.AddRange(encoded);
            else if (Substitutes.TryGetValue(rune.Value, out var substitute))
                output.AddRange(substitute);
            else
            {
                output.Add(Replacement);
                replaced++;
            }
        }

        return [.. output];
    }

    // Barcode data. ESCPOS_NET rejects non-ASCII itself, but passes C0 controls
    // through for CODE128 and GS1-128.
    public static string CleanBarcode(string content, out int replaced)
        => Clean(content, char.IsControl, out replaced);

    // QR data. ESCPOS_NET keeps only the low byte of each char, so a character
    // above U+00FF can also become a control byte (U+0110 -> 0x10 DLE). LF stays:
    // vCard and Wi-Fi payloads use it.
    public static string CleanQRCode(string content, out int replaced)
        => Clean(content.ReplaceLineEndings("\n"), c => c != '\n' && (char.IsControl(c) || c > 0xFF), out replaced);

    private static string Clean(string content, Func<char, bool> isUnsafe, out int replaced)
    {
        replaced = content.Count(isUnsafe);
        return replaced == 0 ? content : string.Concat(content.Select(c => isUnsafe(c) ? '?' : c));
    }
}
