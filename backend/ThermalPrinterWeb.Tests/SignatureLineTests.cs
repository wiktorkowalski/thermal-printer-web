using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;
using ThermalPrinterWeb.Services.Printing;
using static ThermalPrinterWeb.Tests.Support.TestBlocks;

namespace ThermalPrinterWeb.Tests;

// Issue #54: options.sign adds one signature line with the date of the server to a template-mode job (HTTP content, MCP print).
// The line is a Text block that the entry point builds, so the journal stores it and a reprint prints the stored date.
public sealed class SignatureLineTests
{
    private const string PrintUrl = TestHttp.PrintUrl;
    private const string Signed = "2026-10-04 * Claude";

    // Noon UTC: the same date in Poland.
    private static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LaterNoon = new(2026, 12, 24, 12, 0, 0, TimeSpan.Zero);

    // ESC a n
    private static readonly byte[] Center = [0x1B, 0x61, 1];
    private static readonly byte[] Right = [0x1B, 0x61, 2];

    // ESC ! n: bit 0 is Font B.
    private static readonly byte[] Plain = [0x1B, 0x21, 0x00];
    private static readonly byte[] FontB = [0x1B, 0x21, 0x01];

    // GS ! n: width 2 in the high four bits, height 3 in the low four.
    private static readonly byte[] TwoByThree = [0x1D, 0x21, 0x12];
    private static readonly byte[] SizeOff = [0x1D, 0x21, 0x00];

    // GS V 65 3
    private static readonly byte[] CutCommand = [0x1D, 0x56, 0x41, 0x03];

    // The default gap and the auto-cut.
    private static readonly byte[] AutoCut = [0x0A, 0x0A, 0x0A, .. CutCommand];

    // The job of {"content":[{"type":"Text","content":"x"}]} from before the field.
    private static readonly byte[] PlainJob = [.. Prelude, .. BodyBytes("x"), .. AutoCut];

    private static byte[] BodyBytes(string text) => [.. Center, .. Plain, .. Pc852Bytes(text), 0x0A, .. Plain];

    // The date line of the house style: right, Font B, 2 x 3.
    private static byte[] SignatureBytes(string text) => [.. Right, .. FontB, .. TwoByThree, .. Pc852Bytes(text), 0x0A, .. SizeOff, .. Plain];

    private static NoPrinterApp NewApp(DateTimeOffset? now = null) => new(new TestClock(now ?? Noon));

    private static string Json(string blocks, string? options) => $$"""{"content":[{{blocks}}]{{(options is null ? "" : $",\"options\":{options}")}}}""";

    private const string X = """{"type":"Text","content":"x"}""";

    private static string SignOptions(string sign) => $$"""{"sign":{{JsonSerializer.Serialize(sign)}}}""";

    private static string? Error(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetString();

    private static async Task<PrintJob> PrintAsync(TestApp app, string json, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        Assert.True(status == expected, body);
        return Assert.Single(await app.JournalRowsAsync());
    }

    // Opt-in: a job that does not ask sends the bytes of before the field.
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"sign":null}""")]
    [InlineData("""{"sign":""}""")]
    [InlineData("""{"sign":"   "}""")]
    public async Task Print_NoSign_SendsTheBytesOfBeforeTheField(string? options)
    {
        await using var app = NewApp();

        var row = await PrintAsync(app, Json(X, options));

        Assert.Equal(PlainJob, row.Payload.Bytes!);
        Assert.Equal(1, row.BlockCount);
        Assert.Equal("x", row.Payload.PlainText);
    }

    [Fact]
    public async Task McpPrint_NoSign_SendsTheBytesOfBeforeTheField()
    {
        await using var app = NewApp();

        var (isError, answer) = await app.CreateClient().CallToolAsync(PrinterTools.PrintName, Json(X, null));

        Assert.False(isError, answer);
        Assert.Equal(PlainJob, Assert.Single(await app.JournalRowsAsync()).Payload.Bytes!);
    }

    [Fact]
    public async Task Print_Sign_AddsTheLineInTheHouseStyleBeforeTheGapAndTheCut()
    {
        await using var app = NewApp();

        var row = await PrintAsync(app, Json(X, SignOptions("Claude")));

        Assert.Equal([.. Prelude, .. BodyBytes("x"), .. SignatureBytes(Signed), .. AutoCut], row.Payload.Bytes!);
        // The journal holds the line as a block, with its date, and the option as sent.
        Assert.Equal(2, row.BlockCount);
        var line = JsonDocument.Parse(row.Payload.Blocks!).RootElement[1];
        Assert.Equal("Text", line.GetProperty("type").GetString());
        Assert.Equal(Signed, line.GetProperty("content").GetString());
        Assert.Equal("Right", line.GetProperty("alignment").GetString());
        Assert.Equal("x\n" + Signed, row.Payload.PlainText);
        Assert.Equal("x", row.Title);
        Assert.Equal("Claude", JsonDocument.Parse(row.Payload.Options!).RootElement.GetProperty("sign").GetString());
    }

    [Fact]
    public async Task McpPrint_Sign_SendsTheBytesOfTheHttpJob()
    {
        await using var app = NewApp();

        var (isError, answer) = await app.CreateClient().CallToolAsync(PrinterTools.PrintName, Json(X, SignOptions("Claude")));

        Assert.False(isError, answer);
        Assert.Equal("Printed.", answer);
        var row = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal("mcp:print", row.Transport);
        Assert.Equal([.. Prelude, .. BodyBytes("x"), .. SignatureBytes(Signed), .. AutoCut], row.Payload.Bytes!);
        Assert.Equal(2, row.BlockCount);
    }

    // One block builder for the date line of simple mode and the signature line.
    [Fact]
    public void Build_AnyName_IsTheDateLineOfSimpleModeWithTheName()
    {
        var date = new DateOnly(2026, 10, 4);
        var dateLine = SimpleNote.Build("t", "m", date).Last(block => block.Type == ContentType.Text);

        var line = SignatureLine.Build("Claude", date);

        Assert.Equal(dateLine.Content + " * Claude", line.Content);
        Assert.Equal(dateLine.Alignment, line.Alignment);
        Assert.Equal(dateLine.Style, line.Style);
        Assert.Equal((dateLine.Size!.Width, dateLine.Size.Height), (line.Size!.Width, line.Size.Height));
        Assert.Null(line.Wrap);
    }

    // The date is the date in Poland, from the clock of simple mode.
    [Fact]
    public async Task Print_SignLateInTheUtcDay_HasTheDateOfSimpleMode()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 4, 22, 30, 0, TimeSpan.Zero));
        await using var app = new NoPrinterApp(clock);

        var row = await PrintAsync(app, Json(X, SignOptions("Claude")));

        Assert.Equal($"x\n{SimpleNote.DateText(SimpleNote.Today(clock))} * Claude", row.Payload.PlainText);
    }

    // The LineFeed and Cut blocks at the end stay the end.
    [Fact]
    public async Task Print_SignAndContentThatEndsWithFeedAndCut_PutsTheLineBeforeThem()
    {
        await using var app = NewApp();

        var row = await PrintAsync(app, Json($$"""{{X}},{"type":"LineFeed","lines":3},{"type":"Cut"}""", SignOptions("Claude")));

        Assert.Equal(
            [.. Prelude, .. BodyBytes("x"), .. SignatureBytes(Signed), .. Center, 0x0A, 0x0A, 0x0A, .. Center, .. CutCommand],
            row.Payload.Bytes!);
    }

    [Fact]
    public async Task Print_SignWithAutoCutOff_EndsWithTheLine()
    {
        await using var app = NewApp();

        var row = await PrintAsync(app, Json(X, """{"sign":"Claude","autoCut":false}"""));

        Assert.Equal([.. Prelude, .. BodyBytes("x"), .. SignatureBytes(Signed)], row.Payload.Bytes!);
    }

    private static PrintContent Block(ContentType type) => new() { Type = type, Content = "x" };

    // A Cut block in the middle: one line, on the last strip.
    [Theory]
    [InlineData(new[] { ContentType.Text }, 1)]
    [InlineData(new[] { ContentType.Text, ContentType.Cut, ContentType.Text }, 3)]
    [InlineData(new[] { ContentType.Text, ContentType.LineFeed, ContentType.LineFeed, ContentType.Cut }, 1)]
    [InlineData(new[] { ContentType.Text, ContentType.QRCode, ContentType.Signal, ContentType.LineFeed }, 2)]
    [InlineData(new[] { ContentType.Text, ContentType.CodePage }, 1)]
    [InlineData(new[] { ContentType.LineFeed, ContentType.Cut }, 0)]
    [InlineData(new ContentType[0], 0)]
    public void InsertIndex_AnyContent_IsAfterTheLastBlockThatCanPrint(ContentType[] types, int expected)
        => Assert.Equal(expected, SignatureLine.InsertIndex([.. types.Select(Block)]));

    [Fact]
    public void InsertIndex_NullBlockAtTheEnd_IsTheEnd()
        => Assert.Equal(2, SignatureLine.InsertIndex([Block(ContentType.Text), null!]));

    // Simple mode has its date line: the field adds no second one.
    [Fact]
    public async Task Print_SimpleModeWithSign_SendsTheBytesOfTheNoteWithNoSign()
    {
        await using var app = NewApp();
        var expected = await JobBytesAsync(SimpleNote.Build("Title", "Message", new DateOnly(2026, 10, 4)));

        var row = await PrintAsync(app, """{"name":"Title","message":"Message","options":{"sign":"Claude"}}""");

        Assert.Equal(expected, row.Payload.Bytes!);
    }

    [Fact]
    public void MaxSignColumns_OfTheHouseStyle_IsNineteen()
    {
        Assert.Equal(19, SignatureLine.MaxSignColumns);
        // The longest line fills the 32 columns of a body line.
        Assert.Equal(SimpleNote.BodyColumns, SignatureLine.Build(new string('x', SignatureLine.MaxSignColumns), new DateOnly(2026, 10, 4)).Content!.Length);
    }

    [Fact]
    public async Task Print_SignAtTheLimit_Prints()
    {
        await using var app = NewApp();

        var row = await PrintAsync(app, Json(X, SignOptions("Zażółć gęślą jaźń 1")));

        Assert.Equal(JobResult.Printed, row.Result);
        Assert.EndsWith("2026-10-04 * Zażółć gęślą jaźń 1", row.Payload.PlainText);
    }

    public static TheoryData<string, string> BadSigns() => new()
    {
        { Secret[..20], "options.sign length 20 is over the limit of 19" },
        // An arrow prints as two characters.
        { "→→→→→→→→→→", "options.sign length 20 is over the limit of 19" },
        { new string('x', 100_000), "options.sign length 100000 is over the limit of 19" },
        { "Claude\nSECRET", SignatureLine.NotOneLineReason },
        { "Claude\r", SignatureLine.NotOneLineReason },
        { "Cla\tude", SignatureLine.NotOneLineReason },
        { "Cla" + (char)0x1B + "@ude", SignatureLine.NotOneLineReason },
        { "Cla" + (char)0x2028 + "ude", SignatureLine.NotOneLineReason },
        { "Cla" + (char)0x85 + "ude", SignatureLine.NotOneLineReason }
    };

    [Theory]
    [MemberData(nameof(BadSigns))]
    public async Task Print_BadSign_Returns400ThatNamesTheFieldAndRepeatsNoCallerText(string sign, string reason)
    {
        await using var app = NewApp();
        var client = app.CreateClient();

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, Json(X, SignOptions(sign)));
        var (_, answer) = await client.CallToolAsync(PrinterTools.PrintName, Json(X, SignOptions(sign)));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(reason, Error(body));
        Assert.Equal("validation", JsonDocument.Parse(body).RootElement.GetProperty("type").GetString());
        Assert.Equal($"Not printed: {reason}", answer);
        // One Warning per job, with numbers only.
        var warnings = app.Logs.Entries.Where(entry => entry.Message.StartsWith("Rejected print: options.sign", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.Equal(LogLevel.Warning, warning.Level));
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains("SECRET", StringComparison.Ordinal));
        // The rows hold the blocks as sent and no printer data.
        var rows = await app.JournalRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(JobResult.Validation, row.Result);
            Assert.Equal(reason, row.Error);
            Assert.Equal(1, row.BlockCount);
            Assert.Null(row.Payload.Bytes!);
        });
    }

    private static string Blocks(int count) => string.Join(',', Enumerable.Repeat(X, count));

    [Fact]
    public async Task Print_SignAndADocumentAtTheBlockLimit_Returns400ThatNamesTheField()
    {
        await using var app = NewApp();

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, Json(Blocks(PrinterService.MaxBlocks), SignOptions("Claude")));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("options.sign: the signature line is one block, and the document holds 500 blocks already", Error(body));
    }

    // The count in the answer is the count that the caller sent.
    [Fact]
    public async Task Print_SignAndADocumentOverTheBlockLimit_Returns400WithTheCountOfTheCaller()
    {
        await using var app = NewApp();

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, Json(Blocks(PrinterService.MaxBlocks + 1), SignOptions("Claude")));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("block count 501 is over the limit of 500", Error(body));
    }

    [Fact]
    public async Task Print_SignAndOneBlockUnderTheBlockLimit_Prints()
    {
        await using var app = NewApp();

        var row = await PrintAsync(app, Json(Blocks(PrinterService.MaxBlocks - 1), SignOptions("Claude")));

        Assert.Equal(PrinterService.MaxBlocks, row.BlockCount);
    }

    // A fault at a block behind the line has the number of the block in the request.
    [Fact]
    public async Task Print_SignAndAFaultInABlockAfterTheLine_NamesTheBlockNumberOfTheCaller()
    {
        await using var app = NewApp();

        var (status, body) = await app.CreateClient().SendJsonAsync(
            HttpMethod.Post, PrintUrl, Json($$"""{{X}},{"type":"LineFeed","lines":101}""", SignOptions("Claude")));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Block 1 (LineFeed): lines 101 is over the limit of 100", Error(body));
    }

    // The line counts as paper. The fault is at a block that the caller did not send: the answer names the field.
    [Fact]
    public async Task Print_SignThatPassesThePaperLimit_Returns400ThatNamesTheField()
    {
        await using var app = NewApp();
        // 11 x 100 lines and one text line: 31,929 dots. The signature line is 77 more.
        var feeds = string.Join(',', Enumerable.Repeat("""{"type":"LineFeed","lines":100}""", 11));
        var client = app.CreateClient();

        var (plainStatus, plainBody) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, Json($"{feeds},{X}", """{"autoCut":false}"""));
        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, Json($"{feeds},{X}", """{"autoCut":false,"sign":"Claude"}"""));

        Assert.True(plainStatus == HttpStatusCode.OK, plainBody);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("options.sign: the document is over the limit of 32000 dots of paper (4 m)", Error(body));
    }

    [Theory]
    [InlineData("Block 3 (Text): reason", "options.sign: reason")]
    [InlineData("Block 3: must not be null", "options.sign: must not be null")]
    [InlineData("Block 4 (Cut): reason: more", "Block 3 (Cut): reason: more")]
    [InlineData("Block 30 (Cut): reason", "Block 29 (Cut): reason")]
    [InlineData("Block 2 (Text): Block 3 reason", "Block 2 (Text): Block 3 reason")]
    [InlineData("Block count", "Block count")]
    [InlineData("block count 501 is over the limit of 500", "block count 501 is over the limit of 500")]
    [InlineData("options.feedLinesAfterPrint: reason", "options.feedLinesAfterPrint: reason")]
    public void WithCallerBlockNumbers_ValidationFault_CountsWithoutTheLine(string error, string expected)
        => Assert.Equal(expected, SignatureLine.WithCallerBlockNumbers(PrintResult.Invalid(error), 3).Error);

    [Fact]
    public void WithCallerBlockNumbers_ResultThatIsNoValidationFault_IsNotChanged()
    {
        var fault = PrintResult.PrinterFault("Block 3 (Text): reason");

        Assert.Same(PrintResult.Ok, SignatureLine.WithCallerBlockNumbers(PrintResult.Ok, 3));
        Assert.Same(fault, SignatureLine.WithCallerBlockNumbers(fault, 3));
    }

    // A reprint prints the stored date, over HTTP and over MCP, and adds no second line. A new print has the date of its day.
    [Fact]
    public async Task Reprint_SignedJobOnALaterDay_SendsTheBytesOfTheFirstPrint()
    {
        var clock = new TestClock(Noon);
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port) { Clock = clock };
        var client = wired.CreateClient();
        var json = Json(X, SignOptions("Claude"));
        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        var first = await printer.NextJobAsync();
        var id = Assert.Single(await wired.JournalRowsAsync()).Id;

        clock.Now = LaterNoon;
        var (reprintStatus, reprintBody) = await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{id}/reprint");
        var reprinted = await printer.NextJobAsync();
        var (isError, answer) = await client.CallToolAsync(JournalTools.ReprintJobName, $$"""{"id":"{{id}}"}""");
        var reprintedOverMcp = await printer.NextJobAsync();
        await client.SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        var fresh = await printer.NextJobAsync();

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.True(reprintStatus == HttpStatusCode.OK, reprintBody);
        Assert.False(isError, answer);
        Assert.Equal([.. Prelude, .. BodyBytes("x"), .. SignatureBytes(Signed), .. AutoCut], first);
        Assert.Equal(first, reprinted);
        Assert.Equal(first, reprintedOverMcp);
        Assert.Equal(1, reprinted.AsSpan().Count(" * Claude"u8));
        // The clock did move: the same request is another job now.
        Assert.Equal([.. Prelude, .. BodyBytes("x"), .. SignatureBytes("2026-12-24 * Claude"), .. AutoCut], fresh);
    }

    // The job endpoint serves the line as a block: the tray and the journal page draw it with no code of their own.
    [Fact]
    public async Task GetJob_SignedJob_ServesTheLineAsATextBlock()
    {
        await using var app = NewApp();
        var row = await PrintAsync(app, Json(X, SignOptions("Claude")));

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Get, $"{PrintUrl}/jobs/{row.Id}");

        Assert.True(status == HttpStatusCode.OK, body);
        var job = JsonDocument.Parse(body).RootElement;
        Assert.Equal(Signed, job.GetProperty("blocks")[1].GetProperty("content").GetString());
        Assert.Equal("Claude", job.GetProperty("options").GetProperty("sign").GetString());
    }

    // The numbers in the MCP texts are typed by hand.
    [Fact]
    public async Task ToolsList_PrintTool_DescribesTheSignatureLine()
    {
        await using var app = new FakePrinterApp();

        var tools = (await app.CreateClient().McpAsync("tools/list")).GetProperty("tools");
        var print = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == PrinterTools.PrintName);
        var options = print.GetProperty("inputSchema").GetProperty("properties").GetProperty("options");
        var sign = options.GetProperty("properties").GetProperty("sign").GetProperty("description").GetString();

        Assert.Contains($"At most {SignatureLine.MaxSignColumns} characters, one line", sign);
        Assert.Contains("\"yyyy-MM-dd * name\"", sign);
        Assert.Contains("FontB, size 2x3, right", sign);
        Assert.Contains("signature line", options.GetProperty("description").GetString());
        Assert.Contains($"The name holds at most {SignatureLine.MaxSignColumns} characters.", PrinterTools.SignRule);
        Assert.Contains($"size {SimpleNote.Width}x{SimpleNote.Height}", PrinterTools.SignRule);
        Assert.Contains("Without options.sign the server adds no line.", PrinterTools.SignRule);
        Assert.Contains(PrinterTools.SignRule, print.GetProperty("description").GetString());
        Assert.Contains(PrinterTools.SignRule, PrinterTools.ServerInstructions);
    }
}
