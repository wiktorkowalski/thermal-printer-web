using System.Text;
using ESCPOS_NET.Emitters;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;
using BarcodeType = ThermalPrinterWeb.Models.BarcodeType;

namespace ThermalPrinterWeb.Tests;

// QR and barcode content: the bytes the real handlers would send to the printer.
public sealed class CodeContentEncodingTests
{
    // Model (9 bytes), dot size (8) and correction level (8) come before the store command.
    private const int StoreOffset = 25;
    private static readonly byte[] StoreHeader = [0x1D, 0x28, 0x6B];
    private static readonly byte[] StoreFunction = [0x31, 0x50, 0x30];
    private static readonly byte[] PrintCommand = [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30];

    private static async Task<(byte[] Bytes, BlockContext Ctx)> RunQRAsync(string content, QRCodeOptions? options = null)
    {
        // PC852 is active: QR data must not follow the code page.
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);
        await new QRCodeBlockHandler().HandleAsync(
            new PrintContent { Type = ContentType.QRCode, Content = content, QRCodeOptions = options }, ctx);
        return (TestBlocks.OutputBytes(ctx), ctx);
    }

    private static PrintContent Barcode(string content, BarcodeType type)
        => new() { Type = ContentType.Barcode, Content = content, BarcodeOptions = new BarcodeOptions { Type = type } };

    // Walks the store command by its own length prefix. A wrong prefix moves the
    // end of the data, and the print command is no longer what follows it.
    private static (int DeclaredLength, byte[] Data) ReadStoreCommand(byte[] bytes)
    {
        Assert.Equal(StoreHeader, bytes[StoreOffset..(StoreOffset + 3)]);
        var declared = bytes[StoreOffset + 3] | (bytes[StoreOffset + 4] << 8);
        Assert.Equal(StoreFunction, bytes[(StoreOffset + 5)..(StoreOffset + 8)]);

        var dataStart = StoreOffset + 8;
        var dataEnd = dataStart + declared - StoreFunction.Length;
        Assert.Equal(PrintCommand, bytes[dataEnd..]);
        return (declared, bytes[dataStart..dataEnd]);
    }

    public static TheoryData<string> Utf8Contents() =>
    [
        "Zażółć gęślą jaźń",
        "ąćęłńóśźżĄĆĘŁŃÓŚŹŻ",
        "WIFI:T:WPA;S:Sieć Łukasza;P:hasło;;",
        "\U0001F600 ok ✓",
        "日本語のテキスト",
        "https://example.com/path?q=1&r=%C5%BC#frag",
        "café",
        "Đ", // U+0110: the low byte is DLE
        "WIFI:T:WPA;S:Sieć Đorđa;P:hasło;;\nGoście: pokój 2\nDo 22:00 \U0001F600",
        "BEGIN:VCARD\nVERSION:3.0\nFN:Łukasz Żółw\nTEL:+48 600 100 200\nEND:VCARD"
    ];

    [Theory]
    [MemberData(nameof(Utf8Contents))]
    public async Task QRCode_Content_IsStoredAsUtf8(string content)
    {
        var (bytes, ctx) = await RunQRAsync(content);

        var expected = Encoding.UTF8.GetBytes(content);
        var (declared, data) = ReadStoreCommand(bytes);
        Assert.Equal(expected, data);
        Assert.Equal(expected.Length + 3, declared);
        Assert.Equal(0, ctx.ReplacedCharacters);
    }

    [Theory]
    [InlineData(QRCodeModel.Model1, TwoDimensionCodeType.QRCODE_MODEL1, QRCodeSize.Normal, Size2DCode.NORMAL, QRCodeCorrectionLevel.Percent7, CorrectionLevel2DCode.PERCENT_7)]
    [InlineData(QRCodeModel.Model2, TwoDimensionCodeType.QRCODE_MODEL2, QRCodeSize.Large, Size2DCode.LARGE, QRCodeCorrectionLevel.Percent15, CorrectionLevel2DCode.PERCENT_15)]
    [InlineData(QRCodeModel.Model2, TwoDimensionCodeType.QRCODE_MODEL2, QRCodeSize.ExtraLarge, Size2DCode.EXTRA, QRCodeCorrectionLevel.Percent25, CorrectionLevel2DCode.PERCENT_25)]
    [InlineData(QRCodeModel.Micro, TwoDimensionCodeType.QRCODE_MICRO, QRCodeSize.Normal, Size2DCode.NORMAL, QRCodeCorrectionLevel.Percent30, CorrectionLevel2DCode.PERCENT_30)]
    public async Task QRCode_AsciiContent_MatchesTheLibraryBytes(
        QRCodeModel model, TwoDimensionCodeType libraryModel,
        QRCodeSize size, Size2DCode librarySize,
        QRCodeCorrectionLevel level, CorrectionLevel2DCode libraryLevel)
    {
        const string content = "HTTPS://EXAMPLE.COM/1";
        var options = new QRCodeOptions { Model = model, Size = size, CorrectionLevel = level };

        var (bytes, _) = await RunQRAsync(content, options);

        // For ASCII the library was right: same bytes as before this handler built its own commands.
        Assert.Equal(new EPSON().PrintQRCode(content, libraryModel, librarySize, libraryLevel), bytes);
    }

    [Theory]
    [InlineData('A', 252, 0xFF, 0x00)]
    [InlineData('A', 253, 0x00, 0x01)]
    [InlineData('A', 255, 0x02, 0x01)]
    [InlineData('A', 256, 0x03, 0x01)]
    [InlineData('A', 2953, 0x8C, 0x0B)]
    [InlineData('ż', 126, 0xFF, 0x00)] // 252 bytes
    [InlineData('ż', 127, 0x01, 0x01)] // 254 bytes; a char count would give 130
    [InlineData('ż', 128, 0x03, 0x01)] // 256 bytes
    public async Task QRCode_LengthPrefix_CountsBytesAcrossTheByteBoundary(char fill, int characters, byte expectedLow, byte expectedHigh)
    {
        var content = new string(fill, characters);

        var (bytes, _) = await RunQRAsync(content);

        Assert.Equal(expectedLow, bytes[StoreOffset + 3]);
        Assert.Equal(expectedHigh, bytes[StoreOffset + 4]);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), ReadStoreCommand(bytes).Data.Length);
    }

    [Fact]
    public async Task QRCode_VCardLineBreaks_StayAsLineFeeds()
    {
        var (bytes, ctx) = await RunQRAsync("BEGIN:VCARD\r\nFN:Łukasz Żółw\nEND:VCARD");

        Assert.Equal("BEGIN:VCARD\nFN:Łukasz Żółw\nEND:VCARD"u8.ToArray(), ReadStoreCommand(bytes).Data);
        Assert.Equal(0, ctx.ReplacedCharacters);
    }

    // Every C0 character but LF, then DEL, then every C1 character.
    public static TheoryData<int> ControlCodePoints() =>
        [.. Enumerable.Range(0, 0x20).Where(c => c != '\n'), 0x7F, .. Enumerable.Range(0x80, 0x20)];

    [Theory]
    [MemberData(nameof(ControlCodePoints))]
    public async Task QRCode_ControlCharacter_IsRejectedBeforeAnyByte(int codePoint)
    {
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);
        var block = new PrintContent { Type = ContentType.QRCode, Content = "ab" + (char)codePoint + "cd" };

        var ex = await Assert.ThrowsAsync<PrintContentException>(() => new QRCodeBlockHandler().HandleAsync(block, ctx));

        Assert.Equal(PayloadErrorTests.QRCodeControl(codePoint, 2), ex.Message);
        Assert.Empty(ctx.Output);
        Assert.Equal(0, ctx.ReplacedCharacters);
    }

    // The first control character is the one named. A tab or a CR with no LF is not changed to fit.
    [Theory]
    [InlineData("AB\u001dV\u0000", 0x1D, 2)]          // GS V 0: cut
    [InlineData("AB\u0010\u0004\u0001", 0x10, 2)]     // DLE EOT 1: status
    [InlineData("AB\u0010\u0014\u0001\u0000\u0001", 0x10, 2)] // DLE DC4: pulse
    [InlineData("a\r\n\tb", 0x09, 3)]
    [InlineData("a\r\n\r", 0x0D, 3)]                  // CR at the end, after a valid CRLF
    [InlineData("a\n\rb", 0x0D, 2)]                   // LF CR is not CRLF
    public async Task QRCode_CommandSequenceOrStrayCarriageReturn_IsRejected(string content, int codePoint, int index)
    {
        var ex = await Assert.ThrowsAsync<PrintContentException>(() => RunQRAsync(content));

        Assert.Equal(PayloadErrorTests.QRCodeControl(codePoint, index), ex.Message);
    }

    // LS, PS and NBSP are in this range: not control characters, so they stay as they are.
    [Fact]
    public async Task QRCode_EveryCharacterOutsideTheControlRanges_IsStoredUnchangedWithNoControlByte()
    {
        var all = new StringBuilder();
        for (var c = 0x20; c <= 0x2FFF; c++)
        {
            if (c is < 0x7F or > 0x9F)
                all.Append((char)c);
        }
        all.Append("\n\U0001F600");

        var content = all.ToString();
        for (var start = 0; start < content.Length; start += 700)
        {
            var part = content.Substring(start, Math.Min(700, content.Length - start));

            var data = ReadStoreCommand((await RunQRAsync(part)).Bytes).Data;

            Assert.Equal(Encoding.UTF8.GetBytes(part), data);
            Assert.DoesNotContain(data, b => (b < 0x20 && b != 0x0A) || b == 0x7F);
        }
    }

    // UTF-8 has no form for half a pair: each becomes U+FFFD, three bytes above 0x7F.
    [Fact]
    public async Task QRCode_LoneSurrogates_YieldNoControlByteInData()
    {
        var (bytes, _) = await RunQRAsync("\ud83d!\udc00");

        Assert.DoesNotContain(ReadStoreCommand(bytes).Data, b => b < 0x20 || b == 0x7F);
    }

    [Theory]
    [InlineData(QRCodeModel.Model2, 2954, 2953)]
    [InlineData(QRCodeModel.Model1, 708, 707)]
    [InlineData(QRCodeModel.Micro, 22, 21)]
    public async Task QRCode_TooManyBytes_IsRejected(QRCodeModel model, int length, int limit)
    {
        var ex = await Assert.ThrowsAsync<PrintContentException>(
            () => RunQRAsync(new string('A', length), new QRCodeOptions { Model = model }));

        Assert.Equal($"content is {length} bytes as UTF-8; a {model} QR code holds at most {limit}", ex.Message);
    }

    [Theory]
    [InlineData(BarcodeType.CODE128, "Zażółć")]
    [InlineData(BarcodeType.GS1_128, "Zażółć")]
    [InlineData(BarcodeType.CODE39, "ZAŻÓŁĆ")]
    [InlineData(BarcodeType.CODE128, "日本")]
    [InlineData(BarcodeType.CODE128, "A\U0001F600")]
    [InlineData(BarcodeType.CODE128, "12\n34")]
    [InlineData(BarcodeType.CODE128, "AB\u007f")]
    [InlineData(BarcodeType.CODE128, "AB\u0080")]
    public async Task Barcode_ContentOutsidePrintableAscii_IsRejectedBeforeAnyByte(BarcodeType type, string content)
    {
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);
        var block = Barcode(content, type);
        block.BarcodeOptions!.HeightInDots = 80;

        var ex = await Assert.ThrowsAsync<PrintContentException>(() => new BarcodeBlockHandler().HandleAsync(block, ctx));

        Assert.Equal($"a {type} barcode holds printable ASCII only", ex.Message);
        Assert.Empty(ctx.Output);
    }

    [Fact]
    public async Task Barcode_Code128PrintableAscii_IsSentInCodeSetB()
    {
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);

        await new BarcodeBlockHandler().HandleAsync(Barcode("Abc-123 ~", BarcodeType.CODE128), ctx);

        // The last output entry is GS k; height, width and label settings come before it.
        Assert.Equal([0x1D, 0x6B, 0x49, 11, .. "{BAbc-123 ~"u8], ctx.Output[^1]);
        Assert.Equal(0, ctx.ReplacedCharacters);
    }

    [Fact]
    public async Task Barcode_Code128Brace_FillsTheLengthByteExactly()
    {
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);

        // 2 prefix bytes + 252 characters + 1 doubled brace = 255.
        await new BarcodeBlockHandler().HandleAsync(Barcode("{" + new string('A', 251), BarcodeType.CODE128), ctx);

        var bytes = ctx.Output[^1];
        Assert.Equal(0xFF, bytes[3]);
        Assert.Equal(4 + 255, bytes.Length);
    }

    [Theory]
    [InlineData(1, 252)]   // 256 bytes: the length byte would wrap to 0
    [InlineData(126, 2)]   // 256 bytes
    [InlineData(253, 0)]   // 508 bytes
    public async Task Barcode_Code128BracesPastTheLengthByte_AreRejected(int braces, int letters)
    {
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);
        var block = Barcode(new string('{', braces) + new string('A', letters), BarcodeType.CODE128);

        var ex = await Assert.ThrowsAsync<PrintContentException>(() => new BarcodeBlockHandler().HandleAsync(block, ctx));

        Assert.Equal("content is too long for a CODE128 barcode", ex.Message);
        Assert.Empty(ctx.Output);
    }
}
