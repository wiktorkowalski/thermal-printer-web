using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Journal;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Tests;

// Issue #44, owner decision of 2026-10-07: a reprint leaves out a barcode that is wider than the paper.
// The printer dropped that barcode at the first print and the job counted as printed: every receipt of the web UI
// from before the width rule holds one (22 digits in CODE128, 1108 dots). A new print keeps the 400.
public sealed class ReprintWideBarcodeTests
{
    // 22 digits: (22 x 11 + 35) modules x 4 dots = 1108 dots.
    private const string ReceiptDigits = "0123456789012345678901";

    private const string TextBlock = """{"type":"Text","content":"SKLEP\nSUMA 12,34","alignment":"Left"}""";
    private const string WideBarcode =
        $$$"""{"type":"Barcode","content":"{{{ReceiptDigits}}}","alignment":"Center","barcodeOptions":{"type":"CODE128","heightInDots":60,"width":"Default","labelPosition":"Below"}}""";
    private const string FeedAndCut = """{"type":"LineFeed","lines":2},{"type":"Cut"}""";

    // A row like a receipt of the web UI: Text, the barcode, LineFeed, Cut.
    private const string ReceiptBlocks = $"[{TextBlock},{WideBarcode},{FeedAndCut}]";
    private const string ReceiptWithoutBarcode = $"[{TextBlock},{FeedAndCut}]";

    private const string SkipLine = "barcode(s) wider than the paper";

    private static readonly byte[] Center = [0x1B, 0x61, 0x01];

    // GS k: the start of every barcode command. GS h, GS w, GS H: height, module and caption position.
    private static readonly byte[] BarcodeCommand = [0x1D, 0x6B];
    private static readonly byte[][] BarcodeSettings = [[0x1D, 0x68], [0x1D, 0x77], [0x1D, 0x48]];

    // The whole reprint: the job of before the width rule minus the settings and the GS k of the barcode.
    private static readonly byte[] GoldenReprint =
    [
        // ESC @, ESC t 18 (PC852).
        0x1B, 0x40, 0x1B, 0x74, 18,
        // Text: ESC a 0, ESC ! 0, the two lines, ESC ! 0.
        0x1B, 0x61, 0x00, 0x1B, 0x21, 0x00, .. "SKLEP\nSUMA 12,34\n"u8, 0x1B, 0x21, 0x00,
        // Barcode: its alignment command and nothing else.
        0x1B, 0x61, 0x01,
        // LineFeed: ESC a 1 and two lines.
        0x1B, 0x61, 0x01, 0x0A, 0x0A,
        // Cut: ESC a 1, the one line that the two lack for three, GS V 65 3.
        0x1B, 0x61, 0x01, 0x0A, 0x1D, 0x56, 0x41, 0x03
    ];

    private static async Task<Guid> StoreAsync(TestApp app, string blocks, string? options = null)
    {
        var id = Guid.CreateVersion7();
        await app.JournalIdleAsync();
        await using var db = app.JournalDb();
        db.PrintJobs.Add(new PrintJob
        {
            Id = id,
            CreatedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            Transport = "http",
            Source = "web/receipt",
            Result = JobResult.Printed,
            HttpStatus = 200,
            AppVersion = "before-104",
            BlockCount = 4,
            Payload = new PrintJobPayload { JobId = id, Headers = "{}", Blocks = blocks, Options = options }
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static Task<(HttpStatusCode Status, string Body)> ReprintAsync(HttpClient client, Guid id)
        => client.SendJsonAsync(HttpMethod.Post, $"{TestHttp.PrintUrl}/jobs/{id}/reprint");

    private static string? Error(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetString();

    [Fact]
    public async Task Reprint_ReceiptRowWithAWideBarcode_SendsTheJobWithoutTheBarcode()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var client = wired.CreateClient();
        var id = await StoreAsync(wired, ReceiptBlocks);
        // The same job with no Barcode block, as a new print: the reprint differs by the alignment command of the block only.
        var (plainStatus, plainBody) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, $$"""{"content":{{ReceiptWithoutBarcode}}}""");
        var plain = await printer.NextJobAsync();
        wired.Logs.Entries.Clear();

        var (status, body) = await ReprintAsync(client, id);
        var reprinted = await printer.NextJobAsync();

        Assert.True(plainStatus == HttpStatusCode.OK, plainBody);
        Assert.True(status == HttpStatusCode.OK, body);
        // A plain success: no field says that a block was left out.
        Assert.Equal("""{"success":true,"error":null,"type":null}""", body);
        Assert.Equal(GoldenReprint, reprinted);
        Assert.Equal(-1, reprinted.AsSpan().IndexOf(BarcodeCommand));
        Assert.All(BarcodeSettings, setting => Assert.Equal(-1, reprinted.AsSpan().IndexOf(setting)));
        Assert.Equal(-1, reprinted.AsSpan().IndexOf("0123456789"u8));
        Assert.Equal(plain.Length + Center.Length, reprinted.Length);

        var rows = await wired.JournalRowsAsync();
        var plainRow = rows.Single(row => row.Transport == "http" && row.Id != id);
        var reprintRow = rows.Single(row => row.ReprintOf == id);
        Assert.Equal(JobResult.Printed, reprintRow.Result);
        Assert.Equal("http:reprint", reprintRow.Transport);
        // No paper for the barcode.
        Assert.Equal(plainRow.PaperDots, reprintRow.PaperDots);
        Assert.Null(reprintRow.Payload.Blocks);
        Assert.Null(reprintRow.Payload.Bytes);
    }

    [Fact]
    public async Task Reprint_WideBarcode_LogsOneInformationLineWithTheJobIdAndNoWarning()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var client = wired.CreateClient();
        // Two barcodes over the limit and one that fits.
        var fits = WideBarcode.Replace(ReceiptDigits, "ABC123", StringComparison.Ordinal);
        var id = await StoreAsync(wired, $"[{TextBlock},{WideBarcode},{fits},{WideBarcode},{FeedAndCut}]");
        wired.Logs.Entries.Clear();

        var (status, body) = await ReprintAsync(client, id);
        var reprinted = await printer.NextJobAsync();

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(1, reprinted.AsSpan().Count(BarcodeCommand));
        var line = Assert.Single(wired.Logs.Entries, entry => entry.Message.Contains(SkipLine, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal($"Reprint of job {id}: left out 2 barcode(s) wider than the paper", line.Message);
        // The host writes warnings of its own (the request size limit of the test server): none is from the print path.
        Assert.DoesNotContain(wired.Logs.Entries, entry => entry.Level >= LogLevel.Warning && entry.Category.StartsWith("ThermalPrinterWeb", StringComparison.Ordinal));
        Assert.DoesNotContain(wired.Logs.Entries, entry => entry.Message.Contains("Rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(wired.Logs.Entries, entry => entry.Message.Contains(ReceiptDigits, StringComparison.Ordinal));
        Assert.Single(wired.Logs.Entries, entry => entry.Message.StartsWith("Print job:", StringComparison.Ordinal));
    }

    // A reprint of a reprint row reads the blocks of the first job: the same skip, and the log names the first job.
    [Fact]
    public async Task McpReprintJob_WideBarcodeAndAReprintOfTheReprint_AnswersPrinted()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var client = wired.CreateClient();
        var id = await StoreAsync(wired, ReceiptBlocks);

        var (isError, answer) = await client.CallToolAsync(JournalTools.ReprintJobName, $$"""{"id":"{{id}}"}""");
        var first = await printer.NextJobAsync();
        var reprintRow = (await wired.JournalRowsAsync()).Single(row => row.ReprintOf == id);
        wired.Logs.Entries.Clear();
        var (againStatus, againBody) = await ReprintAsync(client, reprintRow.Id);
        var second = await printer.NextJobAsync();

        Assert.False(isError, answer);
        Assert.Equal("Printed.", answer);
        Assert.Equal("mcp:reprint_job", reprintRow.Transport);
        Assert.Equal(GoldenReprint, first);
        Assert.True(againStatus == HttpStatusCode.OK, againBody);
        Assert.Equal(GoldenReprint, second);
        Assert.Single(wired.Logs.Entries, entry => entry.Message == $"Reprint of job {id}: left out 1 barcode(s) wider than the paper");
    }

    // The job endpoint is the same for such a row, before and after its reprint.
    [Fact]
    public async Task GetJob_RowWithAWideBarcode_ServesTheBarcodeAndCanReprint()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var client = wired.CreateClient();
        var id = await StoreAsync(wired, ReceiptBlocks);

        var (_, before) = await client.SendJsonAsync(HttpMethod.Get, $"{TestHttp.PrintUrl}/jobs/{id}");
        var (reprintStatus, reprintBody) = await ReprintAsync(client, id);
        await wired.JournalIdleAsync();
        var (status, after) = await client.SendJsonAsync(HttpMethod.Get, $"{TestHttp.PrintUrl}/jobs/{id}");

        Assert.True(reprintStatus == HttpStatusCode.OK, reprintBody);
        Assert.True(status == HttpStatusCode.OK, after);
        Assert.Equal(before, after);
        var read = JsonDocument.Parse(after).RootElement;
        Assert.True(read.GetProperty("job").GetProperty("canReprint").GetBoolean());
        Assert.Equal(ReceiptDigits, read.GetProperty("blocks")[1].GetProperty("content").GetString());
    }

    // The same blocks as a new print, over HTTP and over MCP, on a host that also reprints: the 400 stays.
    [Fact]
    public async Task NewPrint_WideBarcode_Returns400AlsoAfterAReprint()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var client = wired.CreateClient();
        var id = await StoreAsync(wired, ReceiptBlocks);
        await ReprintAsync(client, id);
        await printer.NextJobAsync();
        const string reason =
            "Block 1 (Barcode): the barcode is at least 1108 dots wide and the paper holds 576; the printer drops a wider barcode: use barcodeOptions.width Thin or shorter content";

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, $$"""{"content":{{ReceiptBlocks}}}""");
        var (_, answer) = await client.CallToolAsync(PrinterTools.PrintName, $$"""{"content":{{ReceiptBlocks}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(reason, Error(body));
        Assert.Equal($"Not printed: {reason}", answer);
        Assert.Empty(printer.Jobs);
    }

    // A new print that got the 400 stores its blocks. Its reprint is a reprint like any other: the job without the barcode.
    // The caller can send that job as a new print, so no limit is passed.
    [Fact]
    public async Task Reprint_RowOfANewPrintThatGotThe400_SendsTheJobWithoutTheBarcode()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var client = wired.CreateClient();
        var (refused, _) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, $$"""{"content":{{ReceiptBlocks}}}""");
        var row = Assert.Single(await wired.JournalRowsAsync());

        var (status, body) = await ReprintAsync(client, row.Id);

        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Equal(JobResult.Validation, row.Result);
        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(GoldenReprint, await printer.NextJobAsync());
    }

    // No field of a request turns a new print into a reprint: the flag comes from the journal trace only.
    [Theory]
    [InlineData("""{"content":[BLOCK],"reprintOf":"01999999-0000-7000-8000-000000000000","isReprint":true}""")]
    [InlineData("""{"content":[BLOCK],"options":{"isReprint":true,"reprintOf":"01999999-0000-7000-8000-000000000000"}}""")]
    public async Task NewPrint_FieldsThatNameAReprint_StillReturns400(string json)
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);

        var (status, body) = await wired.CreateClient().SendJsonAsync(
            HttpMethod.Post, $"{TestHttp.PrintUrl}?reprintOf=01999999-0000-7000-8000-000000000000&isReprint=true",
            json.Replace("BLOCK", WideBarcode.Replace("\"alignment\":\"Center\"", "\"alignment\":\"Center\",\"isReprint\":true", StringComparison.Ordinal), StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.StartsWith("Block 0 (Barcode): the barcode is at least 1108 dots wide", Error(body));
        Assert.Empty(printer.Jobs);
    }

    private const string WideCode128 = """{"type":"Barcode","content":"0123456789012345678901","barcodeOptions":{"type":"CODE128"}}""";

    public static TheoryData<string, string> OtherFaults() => new()
    {
        // The barcode is too wide and holds a character that no barcode holds.
        {
            """[{"type":"Barcode","content":"01234567890123456789żó","barcodeOptions":{"type":"CODE128"}}]""",
            "Block 0 (Barcode): a CODE128 barcode holds printable ASCII only"
        },
        // The height check runs before the width.
        {
            """[{"type":"Barcode","content":"0123456789012345678901","barcodeOptions":{"type":"CODE128","heightInDots":300}}]""",
            "Block 0 (Barcode): barcodeOptions.heightInDots 300 is outside the range 1 to 255"
        },
        // Past the length byte of the command.
        {
            "[{\"type\":\"Barcode\",\"content\":\"" + new string('{', 200) + "\",\"barcodeOptions\":{\"type\":\"CODE128\"}}]",
            "Block 0 (Barcode): content is too long for a CODE128 barcode"
        },
        // A wide barcode in a job that fails another check.
        { "[" + WideCode128 + """,{"type":"LineFeed","lines":101}]""", "Block 1 (LineFeed): lines 101 is over the limit of 100" },
        {
            "[" + WideCode128 + """,{"type":"Text","content":"x","size":{"width":9,"height":1}}]""",
            "Block 1 (Text): size.width 9 is outside the range 1 to 8"
        }
    };

    // Only the width rejection is a skip: every other limit of today rejects the reprint.
    [Theory]
    [MemberData(nameof(OtherFaults))]
    public async Task Reprint_RowThatFailsAnotherCheck_Returns400(string blocks, string reason)
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var id = await StoreAsync(wired, blocks);

        var (status, body) = await ReprintAsync(wired.CreateClient(), id);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(reason, Error(body));
        Assert.Empty(printer.Jobs);
    }

    // The paper limit counts a reprint too: the skipped barcode adds no paper and frees none of the limit for the rest.
    [Fact]
    public async Task Reprint_RowOverThePaperLimit_Returns400()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var feeds = string.Join(',', Enumerable.Repeat("""{"type":"LineFeed","lines":100}""", 12));
        var id = await StoreAsync(wired, $"[{WideBarcode},{feeds}]");

        var (status, body) = await ReprintAsync(wired.CreateClient(), id);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("the document is over the limit of 32000 dots of paper", Error(body));
        Assert.Empty(printer.Jobs);
    }

    // A reprint of a job whose barcode fits is the job of a new print, and logs no skip.
    [Fact]
    public async Task Reprint_RowWithABarcodeThatFits_SendsTheBytesOfANewPrint()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var client = wired.CreateClient();
        var blocks = ReceiptBlocks.Replace(ReceiptDigits, "ABC123", StringComparison.Ordinal);
        var id = await StoreAsync(wired, blocks);
        await client.SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, $$"""{"content":{{blocks}}}""");
        var fresh = await printer.NextJobAsync();

        var (status, body) = await ReprintAsync(client, id);
        var reprinted = await printer.NextJobAsync();

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(fresh, reprinted);
        Assert.Equal(1, reprinted.AsSpan().Count(BarcodeCommand));
        Assert.DoesNotContain(wired.Logs.Entries, entry => entry.Message.Contains(SkipLine, StringComparison.Ordinal));
    }

    // The handler alone: the flag of the context decides between the skip and the rejection.
    [Fact]
    public async Task Handle_WideBarcode_SkipsInAReprintContextAndThrowsInAnyOther()
    {
        var block = new PrintContent
        {
            Type = ContentType.Barcode,
            Content = ReceiptDigits,
            BarcodeOptions = new BarcodeOptions { Type = BarcodeType.CODE128, Width = BarWidth.Thick, HeightInDots = 60 }
        };
        var reprint = new BlockContext(new ESCPOS_NET.Emitters.EPSON(), null) { IsReprint = true };
        var fresh = TestBlocks.NewContext();

        await new BarcodeBlockHandler().HandleAsync(block, reprint);
        var rejected = await Record.ExceptionAsync(() => new BarcodeBlockHandler().HandleAsync(block, fresh));

        Assert.Empty(reprint.Output);
        Assert.Equal(0, reprint.PaperDots);
        Assert.Equal(1, reprint.SkippedBarcodes);
        // No GS w went out, so the module of the job is not the one of the skipped block.
        Assert.Null(reprint.BarModuleDots);
        Assert.IsType<PrintContentException>(rejected);
        Assert.Equal(0, fresh.SkippedBarcodes);
    }

    // Outside a journaled request there is no trace: the build is the build of a new print.
    [Fact]
    public async Task BuildDocumentAsync_NoJournalTrace_RejectsTheWideBarcode()
    {
        var block = new PrintContent { Type = ContentType.Barcode, Content = ReceiptDigits };

        var rejected = await Record.ExceptionAsync(() => TestBlocks.NewService().BuildDocumentAsync([block], null));

        Assert.StartsWith("Block 0 (Barcode): the barcode is at least 1108 dots wide", rejected?.Message);
    }

    [Fact]
    public async Task ToolsList_ReprintJob_SaysThatAWideBarcodeIsLeftOut()
    {
        await using var app = new FakePrinterApp();

        var tools = (await app.CreateClient().McpAsync("tools/list")).GetProperty("tools");
        var reprint = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == JournalTools.ReprintJobName);

        Assert.Equal(
            "One difference: a reprint leaves out a barcode that is wider than the paper, as the first print did.",
            JournalTools.ReprintWideBarcodeNote);
        Assert.Contains(JournalTools.ReprintWideBarcodeNote, reprint.GetProperty("description").GetString());
    }
}
