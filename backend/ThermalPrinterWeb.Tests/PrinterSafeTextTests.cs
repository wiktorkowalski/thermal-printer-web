using System.Text;
using ThermalPrinterWeb.Services.Printing;

namespace ThermalPrinterWeb.Tests;

public class PrinterSafeTextTests
{
    private static readonly Encoding Pc852 = CodePages.GetEncoding("PC852");

    public static TheoryData<string> AllCodePages => [.. CodePages.Names];

    [Fact]
    public void Encode_PlainAscii_IsUnchanged()
    {
        var bytes = PrinterSafeText.Encode("Hello, world!", Pc852, out var replaced);

        Assert.Equal("Hello, world!"u8.ToArray(), bytes);
        Assert.Equal(0, replaced);
    }

    [Fact]
    public void Encode_PolishDiacritics_KeepTheirPc852Bytes()
    {
        var bytes = PrinterSafeText.Encode("ąćęłńóśźżĄĆĘŁŃÓŚŹŻ", Pc852, out var replaced);

        Assert.Equal(
            "A5-86-A9-88-E4-A2-98-AB-BE-A4-8F-A8-9D-E3-E0-97-8D-BD",
            BitConverter.ToString(bytes));
        Assert.Equal(0, replaced);
    }

    [Theory]
    [InlineData("\u001b@", "?@")]                 // ESC @ = reset
    [InlineData("a\u001dV\u0000b", "a?V?b")]      // GS V 0 = cut
    [InlineData("\u0010\u0004\u0001", "???")]     // DLE EOT 1 = real-time status
    [InlineData("\u0007\u0008\u000b\u001a", "????")]
    [InlineData("a\u007fb", "a?b")]               // DEL
    public void Encode_ControlCharacters_BecomeQuestionMarks(string input, string expected)
    {
        var bytes = PrinterSafeText.Encode(input, Pc852, out var replaced);

        Assert.Equal(expected, Encoding.ASCII.GetString(bytes));
        Assert.Equal(expected.Count(c => c == '?'), replaced);
    }

    [Theory]
    [InlineData("one\ntwo", "one\ntwo")]
    [InlineData("one\r\ntwo", "one\ntwo")]
    [InlineData("one\rtwo", "one\ntwo")]
    [InlineData("one\ftwo", "one\ntwo")]
    [InlineData("a\tb", "a b")]
    public void Encode_LineBreaksAndTabs_AreNormalized(string input, string expected)
    {
        var bytes = PrinterSafeText.Encode(input, Pc852, out var replaced);

        Assert.Equal(expected, Encoding.ASCII.GetString(bytes));
        Assert.Equal(0, replaced);
    }

    [Theory]
    [InlineData("a → b", "a -> b")]   // best-fit gives 0x1A
    [InlineData("←", "<-")]           // best-fit gives 0x1B (ESC)
    [InlineData("↔", "<->")]          // best-fit gives 0x1D (GS)
    [InlineData("wait…", "wait...")]  // best-fit gives 0x07 (BEL)
    [InlineData("• item", "* item")]
    public void Encode_BestFitControlBytes_AreTransliterated(string input, string expected)
    {
        var bytes = PrinterSafeText.Encode(input, Pc852, out var replaced);

        Assert.Equal(expected, Encoding.ASCII.GetString(bytes));
        Assert.Equal(0, replaced);
    }

    [Fact]
    public void Encode_BestFitControlByteWithoutSubstitute_BecomesQuestionMark()
    {
        // U+263A (smiley) best-fits to 0x01 in PC852.
        var bytes = PrinterSafeText.Encode("hi ☺", Pc852, out var replaced);

        Assert.Equal("hi ?", Encoding.ASCII.GetString(bytes));
        Assert.Equal(1, replaced);
    }

    [Fact]
    public void Encode_CharacterWithNativeGlyph_IsNotTransliterated()
    {
        // Windows-1250 has a real ellipsis at 0x85.
        var bytes = PrinterSafeText.Encode("…\u001b", CodePages.GetEncoding("WPC1250"), out _);

        Assert.Equal([0x85, (byte)'?'], bytes);
    }

    [Fact]
    public void Encode_Utf8Fallback_KeepsMultiByteAndStripsControls()
    {
        var bytes = PrinterSafeText.Encode("ż\u001b→", Encoding.UTF8, out var replaced);

        Assert.Equal([.. "ż"u8, (byte)'?', .. "→"u8], bytes);
        Assert.Equal(1, replaced);
    }

    [Theory]
    [MemberData(nameof(AllCodePages))]
    public void Encode_AnyBmpCharacter_NeverYieldsControlByte(string codePage)
    {
        var encoding = CodePages.GetEncoding(codePage);
        var all = new StringBuilder();
        for (var c = 0; c <= 0xFFFF; c++)
        {
            if (c != '\n' && c != '\r' && !char.IsSurrogate((char)c))
                all.Append((char)c);
        }

        var bytes = PrinterSafeText.Encode(all.ToString(), encoding, out _);

        // ReplaceLineEndings turns FF, NEL, LS and PS into LF; nothing else may be below 0x20.
        Assert.DoesNotContain(bytes, b => (b < 0x20 && b != 0x0A) || b == 0x7F);
    }

    [Theory]
    [InlineData("ABC-123", "ABC-123", 0)]
    [InlineData("AB\u001b@\u001dV\u0000", "AB?@?V?", 3)]
    [InlineData("12\n34", "12?34", 1)]
    [InlineData("(01)1\u0010\u0004\u0001", "(01)1???", 3)]
    public void CleanBarcode_ReplacesControlCharacters(string input, string expected, int expectedReplaced)
    {
        var cleaned = PrinterSafeText.CleanBarcode(input, out var replaced);

        Assert.Equal(expected, cleaned);
        Assert.Equal(expectedReplaced, replaced);
    }

    [Theory]
    [InlineData("https://example.com/?a=1", "https://example.com/?a=1", 0)]
    [InlineData("WIFI:S:net;\r\nP:pass;;", "WIFI:S:net;\nP:pass;;", 0)]
    [InlineData("a\u001dV\u0000b", "a?V?b", 2)]
    [InlineData("café", "café", 0)]              // Latin-1 passes as one byte
    [InlineData("ĐĄā", "???", 3)]           // low bytes 10 04 01 = DLE EOT 1
    [InlineData("zażółć", "za?ó??", 3)]
    public void CleanQRCode_ReplacesControlAndWideCharacters(string input, string expected, int expectedReplaced)
    {
        var cleaned = PrinterSafeText.CleanQRCode(input, out var replaced);

        Assert.Equal(expected, cleaned);
        Assert.Equal(expectedReplaced, replaced);
    }
}
