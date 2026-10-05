using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;
using static ThermalPrinterWeb.Tests.Support.TestBlocks;

namespace ThermalPrinterWeb.Tests;

// The buzzer and the error light (#48): the beep call and the Signal block.
// The bytes here are golden: ESC B n t is the beep from before the mode, ESC C m t n is read at the printer (2026-10-05).
public sealed class SignalTests(FakePrinterApp app) : IClassFixture<FakePrinterApp>
{
    // The host with the printer replaced: one for the class. A test that reads Beeps clears it first.
    private readonly HttpClient _client = app.CreateClient();

    private const string BeepUrl = "/api/printer/beep";

    private static readonly byte[] CenterAlign = [0x1B, 0x61, 1];
    private static readonly byte[] EscB = [0x1B, 0x42];
    private static readonly byte[] EscC = [0x1B, 0x43];
    private static readonly byte[] LightFourTimes = [0x1B, 0x43, 4, 8, 2];
    private static readonly PrintOptions NoCut = new() { AutoCut = false };

    // A mode with no name would be the n byte of ESC C.
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Build_ModeWithNoName_Throws(int mode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SignalCommand.Build((SignalMode)mode, 1, 1));
    }

    // The n byte of ESC C and the API names: both are fixed.
    [Fact]
    public void SignalMode_NamesAndNumbers_AreFixed()
    {
        Assert.Equal(
            [("Sound", 1), ("Light", 2), ("SoundAndLight", 3)],
            Enum.GetValues<SignalMode>().Select(mode => (mode.ToString(), (int)mode)));
        Assert.Equal(8, (int)ContentType.Signal);
        Assert.Equal(PayloadErrorTests.SignalModeNames, SignalCommand.ModeNames);
    }

    [Theory]
    // A call from before the mode: the same bytes as before.
    [InlineData("", new byte[] { 0x1B, 0x42, 1, 1 })]
    [InlineData("?count=2&duration=3", new byte[] { 0x1B, 0x42, 2, 3 })]
    [InlineData("?count=99&duration=0", new byte[] { 0x1B, 0x42, 9, 1 })]
    [InlineData("?mode=Light&count=4&duration=8", new byte[] { 0x1B, 0x43, 4, 8, 2 })]
    [InlineData("?mode=soundandlight", new byte[] { 0x1B, 0x43, 1, 1, 3 })]
    // The same clamps for every mode.
    [InlineData("?mode=Light&count=20&duration=-1", new byte[] { 0x1B, 0x43, 9, 1, 2 })]
    public async Task PostBeep_OnTheWire_SendsOneCommandAndNothingElse(string query, byte[] expected)
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);

        var response = await wired.CreateClient().PostAsync(BeepUrl + query, null);
        var sent = await printer.NextJobAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, sent);
        // No status read and no second connection.
        Assert.Empty(printer.Queries);
        Assert.Empty(printer.Jobs);
        Assert.Single(wired.PrinterServiceLogs(), entry => entry.Message.StartsWith("Signal sent: mode ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("?count=2&duration=3", SignalMode.Sound)]
    [InlineData("?mode=&count=2&duration=3", SignalMode.Sound)]
    [InlineData("?mode=Sound&count=2&duration=3", SignalMode.Sound)]
    [InlineData("?mode=LIGHT&count=2&duration=3", SignalMode.Light)]
    [InlineData("?mode=SoundAndLight&count=2&duration=3", SignalMode.SoundAndLight)]
    public async Task PostBeep_ModeName_GoesToThePrinterInAnyLetterCase(string query, SignalMode expected)
    {
        app.Printer.Beeps.Clear();

        var response = await _client.PostAsync(BeepUrl + query, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((2, 3, expected), Assert.Single(app.Printer.Beeps));
    }

    [Theory]
    [InlineData("loud")]
    // The enum binder would take these numbers.
    [InlineData("2")]
    [InlineData("7")]
    [InlineData("Sound,Light")]
    [InlineData(TestBlocks.Secret)]
    public async Task PostBeep_UnknownMode_Returns400AndSendsNothing(string mode)
    {
        app.Printer.Beeps.Clear();
        app.Logs.Entries.Clear();

        var response = await _client.PostAsync($"{BeepUrl}?mode={Uri.EscapeDataString(mode)}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            new PrintResponse(false, "mode must be Sound, Light or SoundAndLight", "validation"),
            await response.Content.ReadFromJsonAsync<PrintResponse>());
        Assert.Empty(app.Printer.Beeps);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(TestBlocks.Secret));
    }

    [Theory]
    [InlineData(null, "Beeped 1x.", SignalMode.Sound)]
    [InlineData("""{"count":2,"mode":"Sound"}""", "Beeped 2x.", SignalMode.Sound)]
    [InlineData("""{"count":3,"duration":2,"mode":"Light"}""", "Light flashed 3x.", SignalMode.Light)]
    [InlineData("""{"mode":"SoundAndLight"}""", "Beeped and flashed 1x.", SignalMode.SoundAndLight)]
    [InlineData("""{"mode":null}""", "Beeped 1x.", SignalMode.Sound)]
    // The answer holds the count that went out.
    [InlineData("""{"count":99,"mode":"Light"}""", "Light flashed 9x.", SignalMode.Light)]
    [InlineData("""{"count":0}""", "Beeped 1x.", SignalMode.Sound)]
    public async Task McpBeep_Mode_GoesToThePrinter(string? arguments, string expectedAnswer, SignalMode expectedMode)
    {
        app.Printer.Beeps.Clear();

        var (isError, text) = await _client.CallToolAsync("beep", arguments);

        Assert.False(isError, text);
        Assert.Equal(expectedAnswer, text);
        Assert.Equal(expectedMode, Assert.Single(app.Printer.Beeps).Mode);
    }

    [Theory]
    [InlineData($$$"""{"mode":"{{{TestBlocks.Secret}}}"}""")]
    [InlineData("""{"mode":"3"}""")]
    // A number: the SDK rejects it before the tool body.
    [InlineData("""{"mode":2}""", "'mode' has the wrong JSON type")]
    public async Task McpBeep_UnknownMode_AnswersWithTheValidNamesAndSendsNothing(string arguments, string problem = "'mode' must be Sound, Light or SoundAndLight")
    {
        app.Printer.Beeps.Clear();

        var (isError, text) = await _client.CallToolAsync("beep", arguments);

        Assert.True(isError);
        Assert.StartsWith($"Wrong arguments for 'beep': {problem}. Example of a valid call: {{", text);
        Assert.DoesNotContain(TestBlocks.Secret, text);
        Assert.Empty(app.Printer.Beeps);
    }

    [Theory]
    // A block with no options: one short beep, the bytes of the beep call.
    [InlineData(null, null, null, new byte[] { 0x1B, 0x42, 1, 1 })]
    [InlineData(SignalMode.Sound, 9, 9, new byte[] { 0x1B, 0x42, 9, 9 })]
    [InlineData(SignalMode.Light, 4, 8, new byte[] { 0x1B, 0x43, 4, 8, 2 })]
    [InlineData(SignalMode.SoundAndLight, 2, 1, new byte[] { 0x1B, 0x43, 2, 1, 3 })]
    public async Task BuildDocumentAsync_SignalBlock_IsOneCommandAfterThePrelude(SignalMode? mode, int? count, int? duration, byte[] command)
    {
        var bytes = await JobBytesAsync([Signal(mode, count, duration)], NoCut);

        Assert.Equal([.. Prelude, .. CenterAlign, .. command], bytes);
    }

    [Fact]
    public async Task BuildDocumentAsync_SignalBlockWithNoOptionsObject_IsOneShortBeep()
    {
        var bytes = await JobBytesAsync([new PrintContent { Type = ContentType.Signal }], NoCut);

        Assert.Equal([.. Prelude, .. CenterAlign, 0x1B, 0x42, 1, 1], bytes);
    }

    // The fields of other block types do not reach the printer through a Signal block.
    [Fact]
    public async Task HandleAsync_SignalBlockWithText_AddsNoPaperAndNoText()
    {
        var ctx = NewContext(Pc852);
        var block = Signal(SignalMode.Light, 2, 2);
        block.Content = TestBlocks.Secret;
        block.Lines = 50;

        await new SignalBlockHandler().HandleAsync(block, ctx);

        Assert.Equal(0, ctx.PaperDots);
        Assert.Equal([0x1B, 0x43, 2, 2, 2], OutputBytes(ctx));
        Assert.False(ctx.HasCut);
    }

    [Theory]
    [InlineData(3, null)]
    [InlineData(4, "signal block count 4 is over the limit of 3")]
    public async Task PrintAsync_SignalBlocks_AreLimitedPerDocument(int count, string? expectedError)
    {
        var logger = new RecordingLogger<PrinterService>();
        List<PrintContent> content = [Text(), .. Enumerable.Range(0, count).Select(_ => Signal(SignalMode.SoundAndLight, 9, 9))];
        Assert.Equal(count > PrinterService.MaxSignalBlocks, expectedError is not null);

        var result = await NewService(logger).PrintAsync(content);

        Assert.Equal(expectedError, result.Error);
        if (expectedError is not null)
        {
            Assert.Equal(PrintFailure.Validation, result.Failure);
            Assert.Equal(
                (LogLevel.Warning, "Rejected print: the document has 4 signal blocks, the limit is 3"),
                Assert.Single(logger.Entries, entry => entry.Level >= LogLevel.Warning));
        }
    }

    // The rule of #48: nothing beeps or lights by itself. A job with no Signal block holds no signal command.
    [Fact]
    public async Task BuildDocumentAsync_DocumentWithEveryOtherBlockType_HoldsNoSignalCommand()
    {
        List<PrintContent> content =
        [
            Text("Zażółć gęślą jaźń", PrintStyle.Bold, PrintStyle.Underline, PrintStyle.FontB),
            new() { Type = ContentType.Text, Content = "BIG", Size = new TextSize { Width = 3, Height = 3 } },
            new() { Type = ContentType.Separator },
            new() { Type = ContentType.Barcode, Content = "BOX-0007" },
            new() { Type = ContentType.QRCode, Content = "https://example.com" },
            new() { Type = ContentType.CodePage, Content = "PC437" },
            new() { Type = ContentType.LineFeed, Lines = 2 },
            new() { Type = ContentType.Cut, PartialCut = true }
        ];
        var types = content.Select(block => block.Type).Append(ContentType.Image).Append(ContentType.Signal).Distinct();
        Assert.Equal(Enum.GetValues<ContentType>().Order(), types.Order());

        var bytes = await JobBytesAsync(content, new PrintOptions { DefaultLineSpacing = 40, FeedLinesAfterPrint = 5 });

        Assert.Equal(-1, bytes.AsSpan().IndexOf(EscB));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(EscC));
    }

    [Fact]
    public async Task SimpleNote_Build_HasNoSignalBlockAndNoSignalCommand()
    {
        var note = SimpleNote.Build("Title", "Message", new DateOnly(2026, 10, 5), imageBase64: null);

        Assert.DoesNotContain(note, block => block.Type == ContentType.Signal || block.SignalOptions is not null);
        var bytes = await JobBytesAsync(note);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(EscB));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(EscC));
    }

    // The journal stores the block under its API name; the reprint sends the stored block again and adds no signal.
    [Fact]
    public async Task PrintThenReprint_JobWithASignalBlock_StoresTheBlockAndSendsTheSameBytes()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var client = wired.CreateClient();
        const string json = """{"content":[{"type":"Text","content":"TWOJA KOLEJ"},{"type":"Signal","signalOptions":{"mode":"Light","count":4,"duration":8}}]}""";

        var (printStatus, printBody) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, json);
        var first = await printer.NextJobAsync();
        var job = Assert.Single(await wired.JournalRowsAsync());
        var (reprintStatus, reprintBody) = await client.SendJsonAsync(HttpMethod.Post, $"{TestHttp.PrintUrl}/jobs/{job.Id}/reprint");
        var second = await printer.NextJobAsync();

        Assert.True(printStatus == HttpStatusCode.OK, printBody);
        Assert.True(reprintStatus == HttpStatusCode.OK, reprintBody);
        Assert.Equal(1, first.AsSpan().Count(LightFourTimes));
        Assert.Equal(1, first.AsSpan().Count(EscC));
        Assert.Equal(-1, first.AsSpan().IndexOf(EscB));
        Assert.Equal(first, second);
        Assert.Equal("TWOJA KOLEJ", job.Title);
        Assert.Equal(2, job.BlockCount);
        Assert.Contains("""{"type":"Signal",""", job.Payload.Blocks);
        Assert.Contains("\"signalOptions\":{\"mode\":\"Light\",\"count\":4,\"duration\":8}", job.Payload.Blocks);
        Assert.Equal("TWOJA KOLEJ", job.Payload.PlainText);
    }

    // A job of signals only: no title, no text and no paper in its row.
    [Fact]
    public async Task Print_SignalOnlyJob_StoresARowWithNoTitleAndNoPaper()
    {
        await using var noPrinter = new NoPrinterApp();
        const string json = """{"content":[{"type":"Signal"}],"options":{"autoCut":false}}""";

        var (status, body) = await noPrinter.CreateClient().SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, json);

        Assert.True(status == HttpStatusCode.OK, body);
        var job = Assert.Single(await noPrinter.JournalRowsAsync());
        Assert.Equal(JobResult.Printed, job.Result);
        Assert.Null(job.Title);
        Assert.Null(job.Payload.PlainText);
        Assert.Equal(0, job.PaperDots);
        Assert.Equal(Prelude.Length + CenterAlign.Length + 4, job.ByteCount);
    }

    // One host for the four cases: the real service, so the block checks run.
    [Fact]
    public async Task PostPrinter_SignalBlockTheServerRejects_Returns400()
    {
        await using var closed = new ClosedPortApp();
        var client = closed.CreateClient();
        (string Json, string ErrorStart)[] cases =
        [
            ("""{"content":[{"type":"Signal","signalOptions":{"mode":"Loud"}}]}""", "$.content[0].signalOptions.mode: "),
            ("""{"content":[{"type":"Signal","signalOptions":{"count":"many"}}]}""", "$.content[0].signalOptions.count: "),
            ("""{"content":[{"type":"Signal","signalOptions":{"mode":7}}]}""", "Block 0 (Signal): signalOptions.mode 7 is not a valid value; use Sound, Light or SoundAndLight"),
            ("""{"content":[{"type":"Signal","signalOptions":{"duration":10}}]}""", "Block 0 (Signal): signalOptions.duration 10 is outside the range 1 to 9")
        ];

        foreach (var (json, errorStart) in cases)
        {
            var response = await client.PostAsync(TestHttp.PrintUrl, TestHttp.Json(json));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<PrintResponse>();
            Assert.Equal("validation", body!.Type);
            Assert.StartsWith(errorStart, body.Error);
        }
    }

    [Fact]
    public async Task McpPrint_SignalBlock_ReachesThePrintPath()
    {
        app.Printer.Beeps.Clear();
        app.Printer.Jobs.Clear();
        const string arguments = """{"content":[{"type":"Text","content":"x"},{"type":"Signal","signalOptions":{"mode":"SoundAndLight","count":2}}]}""";

        var (isError, text) = await _client.CallToolAsync("print", arguments);

        Assert.False(isError, text);
        Assert.Equal("Printed.", text);
        Assert.True(app.Printer.Jobs.TryDequeue(out var job));
        var signal = job.Content[1];
        Assert.Equal(ContentType.Signal, signal.Type);
        Assert.Equal((SignalMode.SoundAndLight, 2, 1), (signal.SignalOptions!.Mode, signal.SignalOptions.Count, signal.SignalOptions.Duration));
        // No beep call on the side of a print.
        Assert.Empty(app.Printer.Beeps);
    }
}
