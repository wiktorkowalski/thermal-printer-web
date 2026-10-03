using System.Text;

namespace ThermalPrinterWeb.Services.Printing;

// Caller text must reach the printer as glyphs only. A control byte (ESC, GS,
// DLE ...) in the stream is an ESC/POS command, and DLE real-time commands act
// even inside the data of another command. Two sources: raw C0 characters in
// the input, and best-fit encoding (PC852 maps U+2190 to 0x1B).
internal static class PrinterSafeText
{
    private const byte LineFeed = 0x0A;
    private const byte Replacement = (byte)'?';

    // Readable stand-ins for characters that best-fit turns into a control byte.
    // Used only when the active code page has no glyph for the character.
    private static readonly Dictionary<int, string> Substitutes = new()
    {
        ['\t'] = " ",
        [0x00B7] = ".",   // middle dot
        [0x2022] = "*",   // bullet
        [0x2024] = ".",   // one dot leader
        [0x2026] = "...", // ellipsis
        [0x2190] = "<-",
        [0x2191] = "^",
        [0x2192] = "->",
        [0x2193] = "v",
        [0x2194] = "<->",
        [0x2219] = ".",   // bullet operator
        [0x22C5] = ".",   // dot operator
        [0x25BA] = ">",
        [0x25C4] = "<",
        [0x30FB] = ".",   // katakana middle dot
    };

    // Encodes text for a Text or Separator block. The result has no byte below
    // 0x20 except LF, and no DEL. `replaced` counts characters that became '?'.
    public static byte[] Encode(string text, Encoding encoding, out int replaced)
    {
        replaced = 0;
        var bytes = encoding.GetBytes(text);
        if (!HasControlByte(bytes, allowLineFeed: true))
            return bytes;

        var output = new List<byte>(bytes.Length);
        foreach (var rune in text.ReplaceLineEndings("\n").EnumerateRunes())
        {
            if (rune.Value == LineFeed)
            {
                output.Add(LineFeed);
                continue;
            }

            var encoded = encoding.GetBytes(rune.ToString());
            if (!HasControlByte(encoded, allowLineFeed: false))
            {
                output.AddRange(encoded);
            }
            else if (Substitutes.TryGetValue(rune.Value, out var substitute))
            {
                output.AddRange(Encoding.ASCII.GetBytes(substitute));
            }
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
        replaced = 0;
        var chars = content.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!isUnsafe(chars[i]))
                continue;
            chars[i] = '?';
            replaced++;
        }
        return replaced == 0 ? content : new string(chars);
    }

    private static bool HasControlByte(ReadOnlySpan<byte> bytes, bool allowLineFeed)
    {
        foreach (var b in bytes)
        {
            if (b == LineFeed && allowLineFeed)
                continue;
            if (b < 0x20 || b == 0x7F)
                return true;
        }
        return false;
    }
}
