using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;
using BarcodeType = ThermalPrinterWeb.Models.BarcodeType;

namespace ThermalPrinterWeb.Tests;

// Runs caller content through the real block handlers and checks the bytes
// that would go to the printer.
public sealed class BlockHandlerInjectionTests
{
    private const string Attack = "\u001b@\u001dV\u0000\u0010\u0004\u0001";
    private static readonly byte[] CleanAttack = "?@?V????"u8.ToArray();

    private static async Task<(byte[] Bytes, BlockContext Ctx)> RunAsync(IBlockHandler handler, params PrintContent[] blocks)
    {
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);
        foreach (var block in blocks)
            await handler.HandleAsync(block, ctx);
        return (ctx.OutputBytes(), ctx);
    }

    // Span overloads: a failure prints both byte sequences.
    private static void AssertContains(byte[] expected, byte[] actual)
        => Assert.Contains(expected.AsSpan(), actual.AsSpan());

    private static void AssertDoesNotContain(byte[] expected, byte[] actual)
        => Assert.DoesNotContain(expected.AsSpan(), actual.AsSpan());

    [Fact]
    public async Task Text_ControlCharacters_DoNotReachThePrinter()
    {
        var withAttack = await RunAsync(new TextBlockHandler(), new PrintContent { Type = ContentType.Text, Content = Attack });
        var withClean = await RunAsync(new TextBlockHandler(), new PrintContent { Type = ContentType.Text, Content = "?@?V????" });

        // Same bytes as a job that typed the question marks: nothing of the attack is left.
        Assert.Equal(withClean.Bytes, withAttack.Bytes);
        AssertContains([.. CleanAttack, 0x0A], withAttack.Bytes);
        Assert.Equal(6, withAttack.Ctx.ReplacedCharacters);
        Assert.Equal(0, withClean.Ctx.ReplacedCharacters);
    }

    [Fact]
    public async Task SimpleNote_TitleAndMessage_AreSanitized()
    {
        var note = SimpleNote.Build("Title\u001b@", "Body\u001dV\u0000");
        var textBlocks = note.Where(b => b.Type == ContentType.Text).ToArray();

        var (bytes, ctx) = await RunAsync(new TextBlockHandler(), textBlocks);

        AssertContains("Title?@\n"u8.ToArray(), bytes);
        AssertContains("Body?V?\n"u8.ToArray(), bytes);
        Assert.Equal(3, ctx.ReplacedCharacters);
    }

    [Fact]
    public async Task Separator_ControlCharacter_BecomesQuestionMarks()
    {
        var withAttack = await RunAsync(
            new SeparatorBlockHandler(),
            new PrintContent { Type = ContentType.Separator, SeparatorChar = "\u001b", SeparatorLength = 4 });
        var withClean = await RunAsync(
            new SeparatorBlockHandler(),
            new PrintContent { Type = ContentType.Separator, SeparatorChar = "?", SeparatorLength = 4 });

        Assert.Equal(withClean.Bytes, withAttack.Bytes);
        AssertContains("????\n"u8.ToArray(), withAttack.Bytes);
        Assert.Equal(4, withAttack.Ctx.ReplacedCharacters);
    }

    [Theory]
    [InlineData(BarcodeType.CODE128)]
    [InlineData(BarcodeType.GS1_128)]
    public async Task Barcode_ControlCharacters_RejectTheBlock(BarcodeType type)
    {
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);
        var block = new PrintContent
        {
            Type = ContentType.Barcode,
            Content = "AB" + Attack,
            BarcodeOptions = new BarcodeOptions { Type = type }
        };

        // A barcode with '?' in place of a character is a different barcode: no bytes at all.
        await Assert.ThrowsAsync<PrintContentException>(() => new BarcodeBlockHandler().HandleAsync(block, ctx));
        Assert.Empty(ctx.Output);
    }

    [Fact]
    public async Task QRCode_ControlCharacters_RejectTheBlock()
    {
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);
        var block = new PrintContent { Type = ContentType.QRCode, Content = "AB" + Attack + "ĐĄā" };

        // A QR code with '?' in place of a character scans to other data: no bytes at all.
        var ex = await Assert.ThrowsAsync<PrintContentException>(() => new QRCodeBlockHandler().HandleAsync(block, ctx));

        Assert.StartsWith("content holds the control character U+001B at index 2;", ex.Message);
        Assert.Empty(ctx.Output);
        Assert.Equal(0, ctx.ReplacedCharacters);
    }

    [Fact]
    public async Task QRCode_WideCharacters_AreUtf8NotLowBytes()
    {
        var (bytes, ctx) = await RunAsync(
            new QRCodeBlockHandler(),
            new PrintContent { Type = ContentType.QRCode, Content = "ABĐĄā" });

        // ĐĄā: the low bytes are 10 04 01 (DLE EOT 1). As UTF-8 they are data bytes above 0x7F.
        AssertContains([.. "AB"u8, .. "ĐĄā"u8], bytes);
        AssertDoesNotContain([0x10, 0x04, 0x01], bytes);
        Assert.Equal(0, ctx.ReplacedCharacters);
    }
}
