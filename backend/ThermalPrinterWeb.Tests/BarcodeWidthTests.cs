using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Tests;

// Issue #44: the printer drops a barcode that is wider than the paper, with no error. The server rejects it.
// Read from paper (owner, 2026-10-07, every barcode centered):
//   A CODE128 "ABC123", default width: bars.       B CODE128 "TEST-44-OK", Thin: bars.
//   C CODE128 "TEST-44-OK", default width: nothing. D EAN13 "5901234123457", default width: bars.
public sealed class BarcodeWidthTests(ClosedPortApp app) : IClassFixture<ClosedPortApp>
{
    private const int Paper = 576;

    private static PrintContent Barcode(string content, BarcodeType type = BarcodeType.CODE128, BarWidth? width = BarWidth.Default) => new()
    {
        Type = ContentType.Barcode,
        Content = content,
        BarcodeOptions = new BarcodeOptions { Type = type, Width = width }
    };

    private static string TooWide(int dots, bool offerThin = true)
        => $"the barcode is at least {dots} dots wide and the paper holds {Paper}; the printer drops a wider barcode: "
            + (offerThin ? "use barcodeOptions.width Thin or shorter content" : "use shorter content");

    private static async Task<string?> ErrorAsync(PrintContent block)
        => (await Record.ExceptionAsync(() => new BarcodeBlockHandler().HandleAsync(block, TestBlocks.NewContext(TestBlocks.Pc852))))?.Message;

    [Fact]
    public void MaxDots_IsTheHeadWidth() => Assert.Equal(Paper, BarcodeWidth.MaxDots);

    private static int? Dots(BarcodeType type, string content, BarWidth width)
        => BarcodeWidth.Dots(type, content, BarcodeWidth.ModuleDots(width)!.Value);

    // GS w n as ESCPOS_NET sends it: the model and the bytes hold the same number.
    [Theory]
    [InlineData(BarWidth.Thin, 3)]
    [InlineData(BarWidth.Default, 4)]
    [InlineData(BarWidth.Thick, 5)]
    public async Task ModuleDots_EveryWidth_IsTheNumberInTheWidthCommand(BarWidth width, int dots)
    {
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);

        await new BarcodeBlockHandler().HandleAsync(Barcode("A", width: width), ctx);

        Assert.Equal(dots, BarcodeWidth.ModuleDots(width));
        Assert.Contains(ctx.Output, command => command.AsSpan().SequenceEqual<byte>([0x1D, 0x77, (byte)dots]));
    }

    [Fact]
    public async Task ModuleDots_NoWidth_SendsNoCommandAndCountsTheSmallestModule()
    {
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);

        await new BarcodeBlockHandler().HandleAsync(Barcode("A", width: null), ctx);

        Assert.Null(BarcodeWidth.ModuleDots(null));
        Assert.Null(ctx.BarModuleDots);
        Assert.DoesNotContain(ctx.Output, command => command.Length == 3 && command[0] == 0x1D && command[1] == 0x77);
    }

    // The four barcodes of the paper strip.
    [Theory]
    [InlineData("ABC123", BarcodeType.CODE128, BarWidth.Default, 404)]
    [InlineData("TEST-44-OK", BarcodeType.CODE128, BarWidth.Thin, 435)]
    [InlineData("5901234123457", BarcodeType.EAN13, BarWidth.Default, 380)]
    // Measured with a ruler on 2026-09-27: 61.5 mm = 492 dots.
    [InlineData("BOX-0007", BarcodeType.CODE128, BarWidth.Default, 492)]
    public async Task Handle_BarcodeThatPrintedOnPaper_Passes(string content, BarcodeType type, BarWidth width, int dots)
    {
        Assert.Equal(dots, Dots(type, content, width));
        Assert.Null(await ErrorAsync(Barcode(content, type, width)));
    }

    [Fact]
    public async Task Handle_BarcodeThatThePrinterDropped_IsRejectedBeforeAnyByte()
    {
        var ctx = TestBlocks.NewContext(TestBlocks.Pc852);

        var ex = await Assert.ThrowsAsync<PrintContentException>(() => new BarcodeBlockHandler().HandleAsync(Barcode("TEST-44-OK"), ctx));

        Assert.Equal(580, Dots(BarcodeType.CODE128, "TEST-44-OK", BarWidth.Default));
        Assert.Equal(TooWide(580), ex.Message);
        Assert.Empty(ctx.Output);
        Assert.Equal(0, ctx.PaperDots);
    }

    // A block with no barcodeOptions, or with no width in them, gets the width Default: the model default.
    [Theory]
    [InlineData("""{"type":"Barcode","content":"TEST-44-OK"}""")]
    [InlineData("""{"type":"Barcode","content":"TEST-44-OK","barcodeOptions":{"heightInDots":70}}""")]
    public async Task PostPrinter_TheDroppedBarcodeAsTheOwnerSentIt_Returns400(string block)
    {
        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, $$"""{"content":[{"type":"Text","content":"x"},{{block}}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var response = JsonDocument.Parse(body).RootElement;
        Assert.Equal("validation", response.GetProperty("type").GetString());
        Assert.Equal($"Block 1 (Barcode): {TooWide(580)}", response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task McpPrint_BarcodeWiderThanThePaper_IsNotPrinted()
    {
        var (isError, text) = await app.CreateClient().CallToolAsync("print", """{"content":[{"type":"Barcode","content":"TEST-44-OK"}]}""");

        Assert.False(isError, text);
        Assert.Equal($"Not printed: Block 0 (Barcode): {TooWide(580)}", text);
    }

    // CODE128: 11 modules per character and 35 for start, check symbol and stop. The last length that fits, then the first that does not.
    [Theory]
    [InlineData(BarWidth.Default, 9, 536)]
    [InlineData(BarWidth.Thin, 14, 567)]
    [InlineData(BarWidth.Thick, 7, 560)]
    public async Task Handle_Code128AtTheLastLengthThatFits_PassesAndOneMoreIsRejected(BarWidth width, int length, int dots)
    {
        var perCharacter = BarcodeWidth.Code128ModulesPerCharacter * BarcodeWidth.ModuleDots(width)!.Value;

        Assert.Equal(dots, Dots(BarcodeType.CODE128, new string('A', length), width));
        Assert.Null(await ErrorAsync(Barcode(new string('A', length), width: width)));
        Assert.Equal(
            TooWide(dots + perCharacter, offerThin: width != BarWidth.Thin),
            await ErrorAsync(Barcode(new string('A', length + 1), width: width)));
    }

    // No width: the printer uses its own module, which is not measured. The check counts 2 dots, the smallest value of GS w.
    [Fact]
    public async Task Handle_Code128WithNoWidth_IsRejectedOnlyPastTheSmallestModule()
    {
        Assert.Null(await ErrorAsync(Barcode(new string('A', 23), width: null)));
        Assert.Equal(TooWide(598, offerThin: false), await ErrorAsync(Barcode(new string('A', 24), width: null)));
    }

    // ESCPOS_NET sends '{' twice; it is one symbol on the paper.
    [Fact]
    public async Task Handle_Code128Braces_CountOneSymbolEach()
    {
        Assert.Equal(536, Dots(BarcodeType.CODE128, new string('{', 9), BarWidth.Default));
        Assert.Null(await ErrorAsync(Barcode(new string('{', 9))));
    }

    // The width of these is fixed and under the paper at every bar width: the check never rejects one.
    [Theory]
    [InlineData(BarcodeType.UPC_A, "01234567890", 475)]
    [InlineData(BarcodeType.UPC_A, "012345678905", 475)]
    [InlineData(BarcodeType.EAN13, "590123412345", 475)]
    [InlineData(BarcodeType.EAN13, "5901234123457", 475)]
    [InlineData(BarcodeType.EAN8, "1234567", 335)]
    [InlineData(BarcodeType.EAN8, "12345670", 335)]
    [InlineData(BarcodeType.UPC_E, "012345", 255)]
    [InlineData(BarcodeType.UPC_E, "01234567890", 255)]
    public async Task Handle_FixedLengthSymbologyAtThick_Passes(BarcodeType type, string content, int dots)
    {
        Assert.Equal(dots, Dots(type, content, BarWidth.Thick));
        Assert.Null(await ErrorAsync(Barcode(content, type, BarWidth.Thick)));
    }

    // The printer picks the ratio of wide to narrow bars; the check counts 2, the narrowest. So these widths are a lower bound.
    [Theory]
    // CODE39: 13 modules per character, the two that the printer adds included.
    [InlineData(BarcodeType.CODE39, "ABCDEFGHI", 568, "ABCDEFGHIJ", 620)]
    // The caller sent the start and the stop character: no more are counted.
    [InlineData(BarcodeType.CODE39, "*ABCDEFGHI*", 568, "*ABCDEFGHIJ*", 620)]
    // ITF: 7 modules per digit and 8 for start and stop.
    [InlineData(BarcodeType.ITF, "123456789012345678", 536, "12345678901234567890", 592)]
    // CODABAR: 10 modules per character.
    [InlineData(BarcodeType.CODABAR, "A123456789012B", 556, "A1234567890123B", 596)]
    public async Task Handle_SymbologyWithWideBars_IsRejectedOnlyPastItsNarrowestWidth(BarcodeType type, string fits, int fitsDots, string tooLong, int tooLongDots)
    {
        Assert.Equal(fitsDots, Dots(type, fits, BarWidth.Default));
        Assert.Null(await ErrorAsync(Barcode(fits, type)));
        Assert.Equal(TooWide(tooLongDots), await ErrorAsync(Barcode(tooLong, type)));
    }

    // GS1-128 and GS1 DataBar print no bars on this printer; they keep their bytes (OldFieldTests) and get no width check.
    [Theory]
    [InlineData(BarcodeType.GS1_128, 253)]
    [InlineData(BarcodeType.GS1_DATABAR_OMNIDIRECTIONAL, 13)]
    public async Task Handle_Gs1Type_HasNoWidthCheck(BarcodeType type, int length)
    {
        var content = new string('1', length);

        Assert.Null(Dots(type, content, BarWidth.Thick));
        Assert.Null(await ErrorAsync(Barcode(content, type, BarWidth.Thick)));
    }

    // The length byte first: the text for content that no bar width can save stays the one of before.
    [Fact]
    public async Task Handle_Code128PastTheLengthByte_KeepsTheLengthError()
        => Assert.Equal("content is too long for a CODE128 barcode", await ErrorAsync(Barcode(new string('{', 200))));

    [Fact]
    public async Task PrintAsync_BarcodeWiderThanThePaper_IsLoggedOnceWithNoContent()
    {
        var logger = new RecordingLogger<ThermalPrinterWeb.Services.PrinterService>();

        var result = await TestBlocks.NewService(logger).PrintAsync([Barcode(TestBlocks.Secret)]);

        Assert.Equal($"Block 0 (Barcode): {TooWide(1064)}", result.Error);
        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("block 0 (Barcode)", entry.Message);
        Assert.Contains("1064 dots", entry.Message);
        Assert.DoesNotContain(TestBlocks.Secret, entry.Message);
    }

    // The numbers in the MCP texts are typed by hand: an attribute cannot read the constants.
    [Fact]
    public async Task ToolsList_PrintTool_StatesTheBarcodeWidthsTheCodeApplies()
    {
        static int Code128Characters(BarWidth width)
            => (BarcodeWidth.MaxDots / BarcodeWidth.ModuleDots(width)!.Value - BarcodeWidth.Code128FixedModules) / BarcodeWidth.Code128ModulesPerCharacter;

        var tools = (await app.CreateClient().McpAsync("tools/list")).GetProperty("tools");
        var print = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "print");
        var options = print.GetProperty("inputSchema").GetProperty("properties").GetProperty("content").GetProperty("items").GetProperty("properties")
            .GetProperty("barcodeOptions").GetProperty("properties");
        var (fitsDefault, fitsThin, fitsThick) = (Code128Characters(BarWidth.Default), Code128Characters(BarWidth.Thin), Code128Characters(BarWidth.Thick));

        Assert.Contains(
            $"wider than the paper ({BarcodeWidth.MaxDots} dots) is rejected, because the printer drops it with no error: "
            + $"CODE128 holds {fitsDefault} characters at width Default, {fitsThin} at Thin and {fitsThick} at Thick.",
            options.GetProperty("type").GetProperty("description").GetString());
        Assert.Contains(
            $"Thin ({BarcodeWidth.ThinDots} dots), Default ({BarcodeWidth.DefaultDots} dots) or Thick ({BarcodeWidth.ThickDots} dots). "
            + $"The whole barcode must fit the {BarcodeWidth.MaxDots} dots of the paper",
            options.GetProperty("width").GetProperty("description").GetString());
        Assert.Contains(
            $"A Barcode wider than the paper ({BarcodeWidth.MaxDots} dots) rejects the document, because the printer drops such a barcode with no error: "
            + $"a CODE128 barcode holds {fitsDefault} characters at the default bar width and {fitsThin} with barcodeOptions.width Thin",
            print.GetProperty("description").GetString());
    }

    // GS w holds until the end of the job: a block with "width": null prints with the module of the barcode before it.
    [Fact]
    public async Task BuildDocumentAsync_NoWidthAfterABlockWithAWidth_CountsTheModuleOfThatBlock()
    {
        var thick = Barcode("A", width: BarWidth.Thick);
        var unset = Barcode(new string('A', 8), width: null);

        var alone = await Record.ExceptionAsync(() => TestBlocks.NewService().BuildDocumentAsync([unset, thick], null));
        var afterThick = await Record.ExceptionAsync(() => TestBlocks.NewService().BuildDocumentAsync([thick, unset], null));

        Assert.Null(alone);
        Assert.Equal($"Block 1 (Barcode): {TooWide(615, offerThin: false)}", afterThick?.Message);
    }
}
