using System.Text;
using ThermalPrinterWeb.Services.Printing;

namespace ThermalPrinterWeb.Tests;

public sealed class PrinterSafeTextTests
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

    [Theory]
    [InlineData("a◙b")]         // alone: best-fit gives 0x0A
    [InlineData("a◙b\u001b")]   // next to another control character
    public void Encode_BestFitLineFeed_IsNotALineBreak(string input)
    {
        var bytes = PrinterSafeText.Encode(input, Pc852, out _);

        Assert.DoesNotContain((byte)0x0A, bytes);
        Assert.StartsWith("a?b", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Encode_EmDash_PrintsAsHyphen()
    {
        var bytes = PrinterSafeText.Encode("a — b", Pc852, out var replaced);

        Assert.Equal("a - b", Encoding.ASCII.GetString(bytes));
        Assert.Equal(0, replaced);
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
    public void Encode_AstralAndLoneSurrogates_DoNotThrowOrYieldControlByte(string codePage)
    {
        var bytes = PrinterSafeText.Encode("\u001b\U0001F600\ud83d!\udc00", CodePages.GetEncoding(codePage), out var replaced);

        Assert.DoesNotContain(bytes, b => b < 0x20 || b == 0x7F);
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

        // ReplaceLineEndings turns FF, NEL, LS and PS into LF; nothing else may be a control byte.
        Assert.DoesNotContain(bytes, b => (b < 0x20 && b != 0x0A) || b == 0x7F);
    }

}
