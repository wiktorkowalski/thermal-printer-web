using System.Net;
using System.Text.Json;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;
using static ThermalPrinterWeb.Tests.Support.TestBlocks;

namespace ThermalPrinterWeb.Tests;

// Text size above 2x: the size field, sent as GS ! n (#46).
public sealed class TextSizeTests
{
    private const string PrintUrl = TestHttp.PrintUrl;

    private static readonly PrintOptions NoCut = new() { AutoCut = false };

    // ESC a 1
    private static readonly byte[] Center = [0x1B, 0x61, 1];

    // The start of a job with one centered block.
    private static readonly byte[] JobStart = [.. Prelude, .. Center];

    private static readonly byte[] StylesOff = [0x1B, 0x21, 0x00];
    private static readonly byte[] SizeOff = [0x1D, 0x21, 0x00];

    private static PrintContent Sized(string content, int width, int height, params PrintStyle[] style)
    {
        var block = Text(content, style);
        block.Size = new TextSize { Width = width, Height = height };
        return block;
    }

    private static bool IsSizeCommand(byte[] command) => command is [0x1D, 0x21, _];

    // A block with no size field prints with the bytes from before the field: ESC ! n, the text, ESC ! 0.
    // No GS ! in the job.
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x10, PrintStyle.DoubleHeight)]
    [InlineData(0x20, PrintStyle.DoubleWidth)]
    [InlineData(0x30, PrintStyle.DoubleWidth, PrintStyle.DoubleHeight)]
    [InlineData(0x01, PrintStyle.FontB)]
    [InlineData(0x21, PrintStyle.FontB, PrintStyle.DoubleWidth)]
    [InlineData(0x38, PrintStyle.Bold, PrintStyle.DoubleWidth, PrintStyle.DoubleHeight)]
    public async Task JobBytes_NoSizeField_AreTheBytesFromBeforeTheField(byte escStyle, params PrintStyle[] style)
    {
        var bytes = await TestBlocks.JobBytesAsync([Text("x", style)], NoCut);

        Assert.Equal([.. JobStart, 0x1B, 0x21, escStyle, (byte)'x', 0x0A, 0x1B, 0x21, 0x00], bytes);
    }

    // GS ! n: width - 1 in the high four bits, height - 1 in the low four. The values of the probe strip (#31).
    [Theory]
    [InlineData(1, 1, 0x00)]
    [InlineData(2, 2, 0x11)]
    [InlineData(3, 3, 0x22)]
    [InlineData(4, 4, 0x33)]
    [InlineData(8, 8, 0x77)]
    [InlineData(1, 3, 0x02)]
    [InlineData(3, 1, 0x20)]
    [InlineData(8, 1, 0x70)]
    [InlineData(1, 8, 0x07)]
    public async Task JobBytes_SizeField_SetsTheSizeThenResetsIt(int width, int height, byte n)
    {
        var bytes = await TestBlocks.JobBytesAsync([Sized("x", width, height)], NoCut);

        Assert.Equal([.. JobStart, .. StylesOff, 0x1D, 0x21, n, (byte)'x', 0x0A, .. SizeOff, .. StylesOff], bytes);
    }

    // The size wins: DoubleWidth and DoubleHeight leave ESC ! n, the other styles stay.
    [Theory]
    [InlineData(0x00, PrintStyle.DoubleWidth, PrintStyle.DoubleHeight)]
    [InlineData(0x08, PrintStyle.Bold, PrintStyle.DoubleWidth, PrintStyle.DoubleHeight)]
    [InlineData(0x01, PrintStyle.FontB, PrintStyle.DoubleWidth)]
    [InlineData(0x89, PrintStyle.FontB, PrintStyle.Bold, PrintStyle.Underline, PrintStyle.DoubleHeight)]
    public async Task JobBytes_SizeFieldWithDoubleStyles_UsesTheSizeOnly(byte escStyle, params PrintStyle[] style)
    {
        var bytes = await TestBlocks.JobBytesAsync([Sized("x", 3, 1, style)], NoCut);

        Assert.Equal([.. JobStart, 0x1B, 0x21, escStyle, 0x1D, 0x21, 0x20, (byte)'x', 0x0A, .. SizeOff, .. StylesOff], bytes);
    }

    // A size of 1 x 1 next to DoubleWidth prints at 1 x 1: the field is there, so it decides.
    [Fact]
    public async Task JobBytes_SizeOneByOneWithDoubleWidth_PrintsAtNormalSize()
    {
        var bytes = await TestBlocks.JobBytesAsync([Sized("x", 1, 1, PrintStyle.DoubleWidth)], NoCut);

        Assert.Equal([.. JobStart, .. StylesOff, .. SizeOff, (byte)'x', 0x0A, .. SizeOff, .. StylesOff], bytes);
    }

    [Fact]
    public async Task JobBytes_ReverseAndUpsideDownWithSize_KeepTheirToggleOrder()
    {
        var bytes = await TestBlocks.JobBytesAsync([Sized("x", 2, 3, PrintStyle.ReverseMode, PrintStyle.UpsideDownMode)], NoCut);

        // GS B 1, ESC { 1 ... ESC { 0, GS B 0
        Assert.Equal(
            [.. JobStart, 0x1D, 0x42, 1, 0x1B, 0x7B, 1, .. StylesOff, 0x1D, 0x21, 0x12, (byte)'x', 0x0A, .. SizeOff, .. StylesOff, 0x1B, 0x7B, 0, 0x1D, 0x42, 0],
            bytes);
    }

    [Fact]
    public async Task JobBytes_PolishTextAtThreeByThree_IsInTheCodePageOfTheJob()
    {
        const string Polish = "Zażółć";

        var bytes = await TestBlocks.JobBytesAsync([Sized(Polish, 3, 3)], NoCut);

        Assert.Equal([.. JobStart, .. StylesOff, 0x1D, 0x21, 0x22, .. Pc852Bytes(Polish), 0x0A, .. SizeOff, .. StylesOff], bytes);
    }

    [Fact]
    public async Task JobBytes_SeparatorWithSize_PrintsAtThatSize()
    {
        var separator = new PrintContent { Type = ContentType.Separator, SeparatorLength = 2, Size = new TextSize { Width = 3, Height = 3 } };

        var bytes = await TestBlocks.JobBytesAsync([separator], NoCut);

        Assert.Equal([.. JobStart, .. StylesOff, 0x1D, 0x21, 0x22, (byte)'=', (byte)'=', 0x0A, .. SizeOff, .. StylesOff], bytes);
    }

    // The plain block after a sized block has the bytes of a plain block alone.
    [Fact]
    public async Task JobBytes_PlainTextAfterASizedText_PrintsAtItsOwnSize()
    {
        var alone = await TestBlocks.JobBytesAsync([Text("plain")], NoCut);
        var sized = await TestBlocks.JobBytesAsync([Sized("big", 8, 8)], NoCut);

        var both = await TestBlocks.JobBytesAsync([Sized("big", 8, 8), Text("plain")], NoCut);

        // The second block starts at its alignment command, after the prelude.
        Assert.Equal([.. sized, .. alone[Prelude.Length..]], both);
        Assert.Equal([.. SizeOff, .. StylesOff], sized[^6..]);
    }

    public static TheoryData<PrintContent> BlocksAfterASizedText() => new()
    {
        new PrintContent { Type = ContentType.Separator },
        new PrintContent { Type = ContentType.Barcode, Content = "BOX-0007", BarcodeOptions = new BarcodeOptions { LabelPosition = BarLabelPosition.Both } },
        new PrintContent { Type = ContentType.QRCode, Content = "https://example.com" },
        TestBlocks.ImageBlock(TestImages.PngBase64()),
        new PrintContent { Type = ContentType.LineFeed, Lines = 2 },
        new PrintContent { Type = ContentType.Cut },
        Text("plain", PrintStyle.DoubleWidth)
    };

    // The size is off before the next block starts, whatever its type: its caption, its bars and its feed are at 1 x 1.
    [Theory]
    [MemberData(nameof(BlocksAfterASizedText))]
    public async Task BuildDocumentAsync_AnyBlockAfterASizedText_StartsWithTheSizeOff(PrintContent next)
    {
        var commands = await TestBlocks.NewService().BuildDocumentAsync([Sized("big", 8, 8), next], NoCut);

        // After the two prelude commands, the text block: ESC a, ESC !, GS ! n, text, GS ! 0, ESC ! 0.
        Assert.Equal([Reset, SelectPc852, Center, StylesOff, [0x1D, 0x21, 0x77], "big\n"u8.ToArray(), SizeOff, StylesOff], commands[..8]);
        // The next block: its alignment first, and no size command of its own.
        Assert.Equal(Center, commands[8]);
        Assert.DoesNotContain(commands[8..], IsSizeCommand);
    }

    [Theory]
    [InlineData(0, 1, "size.width 0 is outside the range 1 to 8")]
    [InlineData(9, 1, "size.width 9 is outside the range 1 to 8")]
    [InlineData(-1, 1, "size.width -1 is outside the range 1 to 8")]
    [InlineData(int.MaxValue, 1, "size.width 2147483647 is outside the range 1 to 8")]
    [InlineData(1, 0, "size.height 0 is outside the range 1 to 8")]
    [InlineData(1, 9, "size.height 9 is outside the range 1 to 8")]
    [InlineData(1, 16, "size.height 16 is outside the range 1 to 8")]
    // The first field at fault is named.
    [InlineData(9, 9, "size.width 9 is outside the range 1 to 8")]
    public async Task PrintAsync_SizeOutsideTheRange_IsAValidationFailure(int width, int height, string reason)
    {
        var text = await TestBlocks.NewService().PrintAsync([Text("ok"), Sized(TestBlocks.Secret, width, height)]);
        var separator = await TestBlocks.NewService().PrintAsync(
            [new PrintContent { Type = ContentType.Separator, Size = new TextSize { Width = width, Height = height } }]);

        Assert.Equal(PrintResult.Invalid($"Block 1 (Text): {reason}"), text);
        Assert.Equal(PrintResult.Invalid($"Block 0 (Separator): {reason}"), separator);
    }

    // A block type that prints no text does not read the field.
    [Fact]
    public async Task BuildDocumentAsync_SizeOnABlockWithNoText_IsNotRead()
    {
        var feed = new PrintContent { Type = ContentType.LineFeed, Size = new TextSize { Width = 99, Height = 99 } };

        var commands = await TestBlocks.NewService().BuildDocumentAsync([feed], NoCut);

        Assert.DoesNotContain(commands, IsSizeCommand);
    }

    // 576 dots, a 12-dot cell in Font A and a 9-dot cell in Font B: whole characters only.
    [Theory]
    [InlineData(1, 48, 64)]
    [InlineData(2, 24, 32)]
    [InlineData(3, 16, 21)]
    [InlineData(4, 12, 16)]
    [InlineData(5, 9, 12)]
    [InlineData(6, 8, 10)]
    [InlineData(7, 6, 9)]
    [InlineData(8, 6, 8)]
    public void Columns_EveryWidth_IsTheHeadWidthOverTheCellWidth(int width, int fontA, int fontB)
    {
        const int HeadDots = 576;

        Assert.Equal(fontA, PaperLength.Columns(fontB: false, width));
        Assert.Equal(fontB, PaperLength.Columns(fontB: true, width));
        Assert.Equal(HeadDots / (12 * width), fontA);
        Assert.Equal(HeadDots / (9 * width), fontB);
    }

    // One line is 29 dots plus 24 for each step of the height. The width sets where a line wraps.
    [Theory]
    // 1 x 1 and the two styles: the values from before the field.
    [InlineData(48, 0, 0, false, 29)]
    [InlineData(49, 0, 0, false, 58)]
    [InlineData(1, 1, 2, false, 53)]
    // 3 x 3: 16 columns, 77 dots per line.
    [InlineData(16, 3, 3, false, 77)]
    [InlineData(17, 3, 3, false, 154)]
    // 1 x 3: 48 columns, tall.
    [InlineData(48, 1, 3, false, 77)]
    [InlineData(49, 1, 3, false, 154)]
    // 3 x 1: 16 columns, low.
    [InlineData(16, 3, 1, false, 29)]
    [InlineData(17, 3, 1, false, 58)]
    // 8 x 8: 6 columns, 197 dots per line.
    [InlineData(6, 8, 8, false, 197)]
    [InlineData(7, 8, 8, false, 394)]
    // 5 x 1: 9 columns, not 9.6.
    [InlineData(9, 5, 1, false, 29)]
    [InlineData(10, 5, 1, false, 58)]
    // Font B at 3 x 3: 21 columns.
    [InlineData(21, 3, 3, true, 77)]
    [InlineData(22, 3, 3, true, 154)]
    public async Task HandleAsync_TextAtASize_CountsTheTallerAndShorterLines(int characters, int width, int height, bool fontB, int expectedDots)
    {
        var block = Text(new string('x', characters), fontB ? [PrintStyle.FontB] : []);
        if (width > 0)
            block.Size = new TextSize { Width = width, Height = height };
        var ctx = TestBlocks.NewContext();

        await new TextBlockHandler().HandleAsync(block, ctx);

        Assert.Equal(expectedDots, ctx.PaperDots);
    }

    // The size field and the two styles give the same paper for the same size.
    [Fact]
    public async Task HandleAsync_DoubleStylesAndSizeTwoByTwo_CountTheSamePaper()
    {
        var content = new string('x', 100) + "\n\nend";
        var styled = TestBlocks.NewContext();
        var sized = TestBlocks.NewContext();

        await new TextBlockHandler().HandleAsync(Text(content, PrintStyle.DoubleWidth, PrintStyle.DoubleHeight), styled);
        await new TextBlockHandler().HandleAsync(Sized(content, 2, 2), sized);

        // 5 + 1 + 1 lines of 53 dots.
        Assert.Equal(371, styled.PaperDots);
        Assert.Equal(styled.PaperDots, sized.PaperDots);
    }

    // The 4 m limit counts the tall lines: 162 lines of 197 dots are 31,914 dots.
    [Theory]
    [InlineData(162, true)]
    [InlineData(163, false)]
    public async Task BuildDocumentAsync_TallLines_AreLimitedByThePaper(int lines, bool accepted)
    {
        var block = Sized(new string('\n', lines - 1), 8, 8);

        var build = TestBlocks.NewService().BuildDocumentAsync([block], null);

        if (accepted)
            Assert.NotEmpty(await build);
        else
            Assert.Equal(PayloadErrorTests.OverPaper(0, ContentType.Text), (await Assert.ThrowsAsync<PrintContentException>(() => build)).Message);
    }

    // The line limit of a block stays: 500 lines at 1 x 1 pass, the same lines at 1 x 3 are over the paper limit.
    [Fact]
    public async Task BuildDocumentAsync_FiveHundredLines_PassAtNormalSizeOnly()
    {
        var content = new string('\n', TextBlockHandler.MaxLines - 1);

        Assert.NotEmpty(await TestBlocks.NewService().BuildDocumentAsync([Text(content)], null));
        var exception = await Assert.ThrowsAsync<PrintContentException>(
            () => TestBlocks.NewService().BuildDocumentAsync([Sized(content, 1, 3)], null));
        Assert.Equal(PayloadErrorTests.OverPaper(0, ContentType.Text), exception.Message);
    }

    private static string JobJson(string size)
        => """{"content":[{"type":"Text","content":"Big","size":SIZE}],"options":{"autoCut":false}}""".Replace("SIZE", size, StringComparison.Ordinal);

    // The JSON of the API, through the real pipeline: the size reaches the printer bytes, the journal and a reprint.
    [Fact]
    public async Task PostPrinter_SizeField_ReachesTheBytesTheJournalAndAReprint()
    {
        await using var printer = new WirePrinter();
        await using var app = new LoopbackPrinterApp(printer.Port);
        var client = app.CreateClient();

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, JobJson("""{"width":3,"height":3}"""));
        var first = await printer.NextJobAsync();
        var job = Assert.Single(await app.JournalRowsAsync());
        var (reprintStatus, reprintBody) = await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{job.Id}/reprint");
        var second = await printer.NextJobAsync();

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.True(reprintStatus == HttpStatusCode.OK, reprintBody);
        Assert.Equal([.. JobStart, .. StylesOff, 0x1D, 0x21, 0x22, .. "Big\n"u8, .. SizeOff, .. StylesOff], first);
        Assert.Equal(first, second);
        Assert.Contains("\"size\":{\"width\":3,\"height\":3}", job.Payload.Blocks);
        // One line of 29 + 2 x 24 dots.
        Assert.Equal(77, job.PaperDots);
    }

    // One host for the cases of a test: each case reads the newest journal row.
    [Fact]
    public async Task PostPrinter_SizeFormsOfTheJson_GiveTheSizeCommandOrNone()
    {
        const string NoSizeField = """{"content":[{"type":"Text","content":"Big"}],"options":{"autoCut":false}}""";
        (string Json, byte? N)[] cases =
        [
            // One axis: the other is 1.
            (JobJson("""{"width":3}"""), 0x20),
            (JobJson("""{"height":3}"""), 0x02),
            (JobJson("{}"), 0x00),
            // No size, or null: ESC ! n only, no GS !.
            (NoSizeField, null),
            (JobJson("null"), null)
        ];
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();

        foreach (var (json, n) in cases)
        {
            var (status, body) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, json);

            Assert.True(status == HttpStatusCode.OK, body);
            byte[] expected = n is { } size
                ? [.. JobStart, .. StylesOff, 0x1D, 0x21, size, .. "Big\n"u8, .. SizeOff, .. StylesOff]
                : [.. JobStart, .. StylesOff, .. "Big\n"u8, .. StylesOff];
            Assert.Equal(expected, (await app.JournalRowsAsync())[^1].Payload.Bytes);
        }
    }

    [Fact]
    public async Task PostPrinter_SizeNotValid_Answers400WithTheReason()
    {
        (string Size, string Error)[] cases =
        [
            ("""{"width":9,"height":1}""", "Block 0 (Text): size.width 9 is outside the range 1 to 8"),
            ("""{"width":1,"height":0}""", "Block 0 (Text): size.height 0 is outside the range 1 to 8"),
            // Whole numbers only: a fraction does not bind.
            ("""{"width":2.5,"height":1}""", "$.content[0].size.width: malformed JSON, wrong JSON type or unknown name"),
            ("""{"width":1,"height":null}""", "$.content[0].size.height: malformed JSON, wrong JSON type or unknown name"),
            ("3", "$.content[0].size: malformed JSON, wrong JSON type or unknown name")
        ];
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();

        foreach (var (size, error) in cases)
        {
            var (status, body) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, JobJson(size));

            Assert.Equal(HttpStatusCode.BadRequest, status);
            var response = JsonDocument.Parse(body).RootElement;
            Assert.Equal(error, response.GetProperty("error").GetString());
            Assert.Equal("validation", response.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task McpPrint_SizeField_FollowsTheSameRules()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();

        var (_, printed) = await client.CallToolAsync("print", JobJson("""{"width":3,"height":3}"""));
        var (_, refused) = await client.CallToolAsync("print", JobJson("""{"width":9,"height":3}"""));

        Assert.Equal("Printed.", printed);
        Assert.Equal("Not printed: Block 0 (Text): size.width 9 is outside the range 1 to 8", refused);
    }
}
