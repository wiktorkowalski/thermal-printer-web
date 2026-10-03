using ESCPOS_NET.Emitters;
using ESCPOS_NET.Utilities;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;
using BarcodeType = ThermalPrinterWeb.Models.BarcodeType;

namespace ThermalPrinterWeb.Tests;

// Runs caller content through the real block handlers and checks the bytes
// that would go to the printer.
public class BlockHandlerInjectionTests
{
    private const string Attack = "\u001b@\u001dV\u0000\u0010\u0004\u0001";
    private static readonly byte[] CleanAttack = "?@?V????"u8.ToArray();

    private static BlockContext NewContext()
        => new(new EPSON(), null) { Encoding = CodePages.GetEncoding("PC852") };

    private static async Task<(byte[] Bytes, BlockContext Ctx)> RunAsync(IBlockHandler handler, params PrintContent[] blocks)
    {
        var ctx = NewContext();
        foreach (var block in blocks)
            await handler.HandleAsync(block, ctx);
        return (ByteSplicer.Combine([.. ctx.Output]), ctx);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public async Task Text_ControlCharacters_DoNotReachThePrinter()
    {
        var withAttack = await RunAsync(new TextBlockHandler(), new PrintContent { Type = ContentType.Text, Content = Attack });
        var withClean = await RunAsync(new TextBlockHandler(), new PrintContent { Type = ContentType.Text, Content = "?@?V????" });

        // Same bytes as a job that typed the question marks: nothing of the attack is left.
        Assert.Equal(withClean.Bytes, withAttack.Bytes);
        Assert.True(Contains(withAttack.Bytes, [.. CleanAttack, 0x0A]));
        Assert.Equal(6, withAttack.Ctx.ReplacedCharacters);
        Assert.Equal(0, withClean.Ctx.ReplacedCharacters);
    }

    [Fact]
    public async Task SimpleNote_TitleAndMessage_AreSanitized()
    {
        var note = SimpleNote.Build("Title\u001b@", "Body\u001dV\u0000");
        var textBlocks = note.Where(b => b.Type == ContentType.Text).ToArray();

        var (bytes, ctx) = await RunAsync(new TextBlockHandler(), textBlocks);

        Assert.True(Contains(bytes, "Title?@\n"u8.ToArray()));
        Assert.True(Contains(bytes, "Body?V?\n"u8.ToArray()));
        Assert.Equal(3, ctx.ReplacedCharacters);
    }

    [Fact]
    public async Task Separator_ControlCharacter_BecomesQuestionMarks()
    {
        var (bytes, ctx) = await RunAsync(
            new SeparatorBlockHandler(),
            new PrintContent { Type = ContentType.Separator, SeparatorChar = "\u001b", SeparatorLength = 4 });

        Assert.True(Contains(bytes, "????\n"u8.ToArray()));
        Assert.DoesNotContain((byte)0x1B, bytes.AsSpan(bytes.AsSpan().IndexOf("????"u8), 4).ToArray());
        Assert.Equal(4, ctx.ReplacedCharacters);
    }

    [Theory]
    [InlineData(BarcodeType.CODE128)]
    [InlineData(BarcodeType.GS1_128)]
    public async Task Barcode_ControlCharacters_AreReplacedInPayload(BarcodeType type)
    {
        var (bytes, ctx) = await RunAsync(
            new BarcodeBlockHandler(),
            new PrintContent
            {
                Type = ContentType.Barcode,
                Content = "AB" + Attack,
                BarcodeOptions = new BarcodeOptions { Type = type }
            });

        Assert.True(Contains(bytes, [.. "AB"u8, .. CleanAttack]));
        Assert.False(Contains(bytes, [0x10, 0x04, 0x01]));
        Assert.Equal(6, ctx.ReplacedCharacters);
    }

    [Fact]
    public async Task QRCode_ControlAndWideCharacters_AreReplacedInPayload()
    {
        var (bytes, ctx) = await RunAsync(
            new QRCodeBlockHandler(),
            new PrintContent { Type = ContentType.QRCode, Content = "AB" + Attack + "ĐĄā" });

        Assert.True(Contains(bytes, [.. "AB"u8, .. CleanAttack, .. "???"u8]));
        Assert.False(Contains(bytes, [0x10, 0x04, 0x01]));
        Assert.Equal(9, ctx.ReplacedCharacters);
    }
}
