using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Tests;

// DLE EOT 3 (error status), with the real PrinterService against a fake printer on a loopback port.
public sealed class PrinterErrorStatusTests
{
    private const string StatusUrl = "/api/printer/status";
    private const string PrintUrl = "/api/printer";
    private const string PrintJson = TestHttp.PrintJson;
    private const string IdleRaw = "n1=16 n2=12 n4=12 n3=12";

    private const int CutterBit = 0x08;
    private const int UnrecoverableBit = 0x20;
    private const int AutoRecoverableBit = 0x40;
    private const int RecoverableBit = 0x04;

    private static string Json(bool value) => value ? "true" : "false";

    // The whole wire shape of a status with no cover or paper fault.
    private static string StatusJson(string raw, int errorBits = 0, string? reason = null)
        => $$"""
            {"reachable":true,"online":true,"coverOpen":false,"paperOut":false,"paperLow":false,"raw":"{{raw}}",
            "cutterError":{{Json((errorBits & CutterBit) != 0)}},
            "unrecoverableError":{{Json((errorBits & UnrecoverableBit) != 0)}},
            "autoRecoverableError":{{Json((errorBits & AutoRecoverableBit) != 0)}},
            "recoverableError":{{Json((errorBits & RecoverableBit) != 0)}},
            "ready":{{Json(reason is null)}},"notReadyReason":{{(reason is null ? "null" : $"\"{reason}\"")}}}
            """.ReplaceLineEndings("");

    [Theory]
    [InlineData(0x12, null)]
    [InlineData(0x1A, "cutter error")]
    [InlineData(0x32, "unrecoverable error")]
    [InlineData(0x52, "auto-recoverable error")]
    [InlineData(0x16, "recoverable error")]
    // Combined: the reason names one cause, every flag is set.
    [InlineData(0x1E, "cutter error")]
    [InlineData(0x72, "unrecoverable error")]
    [InlineData(0x56, "auto-recoverable error")]
    [InlineData(0x7E, "cutter error")]
    public async Task GetStatus_ErrorStatusByte_SetsTheFlagsAndTheReason(int n3, string? reason)
    {
        using var printer = new FakeStatusPrinter { N3 = (byte)n3 };
        using var app = new LoopbackPrinterApp(printer.Port);

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Get, StatusUrl);

        // 503 is for a printer that does not answer.
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(StatusJson($"n1=16 n2=12 n4=12 n3={n3:x2}", n3, reason), body);
    }

    // Query 3 goes last: its late answer cannot be read as the answer to another query.
    [Fact]
    public async Task GetStatus_AnyRead_SendsTheFourQueriesInOneConnectionWithTheErrorQueryLast()
    {
        using var printer = new FakeStatusPrinter();
        using var app = new LoopbackPrinterApp(printer.Port);

        await app.CreateClient().SendJsonAsync(HttpMethod.Get, StatusUrl);

        Assert.Equal([1, 2, 4, 3], printer.Queries);
    }

    // The web UI polls the status: a healthy read writes no line at Information or above.
    [Fact]
    public async Task GetStatus_HealthyPrinterPolled_LogsNothingAboveDebug()
    {
        using var printer = new FakeStatusPrinter();
        using var app = new LoopbackPrinterApp(printer.Port);
        var client = app.CreateClient();

        for (var poll = 0; poll < 3; poll++)
        {
            var (_, body) = await client.SendJsonAsync(HttpMethod.Get, StatusUrl);
            Assert.Equal(StatusJson(IdleRaw), body);
        }

        Assert.Empty(app.PrinterServiceLogs());
        // Nor does the controller, or any other category, write a line per read.
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Level >= LogLevel.Information && entry.Message.Contains("status", StringComparison.OrdinalIgnoreCase));
    }

    // Not a status frame (bits 1 and 4 set, bits 0 and 7 clear): the error status is unknown.
    // 0xFF and 0x7F have every error bit set.
    [Theory]
    [InlineData(0xFF)]
    [InlineData(0x7F)]
    [InlineData(0x00)]
    [InlineData(0x1B)]
    [InlineData(0x0A)]
    [InlineData(0x9A)]
    public async Task GetStatus_ErrorQueryAnswersNoStatusFrame_KeepsReadyAndSetsNoFlag(int n3)
    {
        using var printer = new FakeStatusPrinter { N3 = (byte)n3 };
        using var app = new LoopbackPrinterApp(printer.Port);

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Get, StatusUrl);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(StatusJson($"n1=16 n2=12 n4=12 n3={n3:x2}!"), body);
        Assert.Empty(app.PrinterServiceLogs());
    }

    [Fact]
    public async Task GetStatus_ErrorQueryGetsNoAnswer_KeepsReadyAfterOneReadTimeout()
    {
        using var printer = new FakeStatusPrinter { N3 = null };
        using var app = new LoopbackPrinterApp(printer.Port);
        var client = app.CreateClient();
        var time = Stopwatch.StartNew();

        var (status, body) = await client.SendJsonAsync(HttpMethod.Get, StatusUrl);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(StatusJson("n1=16 n2=12 n4=12 n3=?"), body);
        Assert.Empty(app.PrinterServiceLogs());
        // One read timeout of 2 s, not one per query.
        Assert.InRange(time.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3.9));
    }

    [Fact]
    public async Task GetStatus_PrinterClosesTheConnectionAtTheErrorQuery_KeepsReady()
    {
        using var printer = new FakeStatusPrinter { DropsAtErrorQuery = true };
        using var app = new LoopbackPrinterApp(printer.Port);

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Get, StatusUrl);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(StatusJson("n1=16 n2=12 n4=12 n3=?"), body);
        Assert.Empty(app.PrinterServiceLogs());
    }

    // The bytes of the real printer with its cover open (2026-06-28).
    [Theory]
    [InlineData(0x12, false)]
    [InlineData(0xFF, false)]
    [InlineData(0x1A, true)]
    public async Task GetStatus_CoverOpen_StaysTheReasonWhateverTheErrorQueryAnswers(int n3, bool cutterError)
    {
        using var printer = new FakeStatusPrinter { N1 = 0x1E, N2 = 0x36, N3 = (byte)n3 };
        using var app = new LoopbackPrinterApp(printer.Port);

        var (_, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Get, StatusUrl);

        Assert.Contains("\"coverOpen\":true", body);
        Assert.Contains($"\"cutterError\":{Json(cutterError)}", body);
        Assert.Contains("\"ready\":false,\"notReadyReason\":\"cover open\"", body);
    }

    [Theory]
    [InlineData(0x1A, "cutter error")]
    [InlineData(0x32, "unrecoverable error")]
    [InlineData(0x52, "auto-recoverable error")]
    [InlineData(0x16, "recoverable error")]
    public async Task PostPrinter_ErrorBitSet_Returns503SendsNoJobAndStoresTheStatus(int n3, string reason)
    {
        using var printer = new FakeStatusPrinter { N3 = (byte)n3 };
        await using var app = new LoopbackPrinterApp(printer.Port);

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal($$"""{"success":false,"error":"Printer not ready: {{reason}}","type":"printer"}""", body);
        Assert.Empty(printer.Jobs);
        Assert.Equal([1, 2, 4, 3], printer.Queries);

        var refusal = Assert.Single(app.PrinterServiceLogs());
        Assert.Equal(LogLevel.Warning, refusal.Level);
        Assert.Equal($"Refusing print: printer not ready ({reason}); status=n1=16 n2=12 n4=12 n3={n3:x2}", refusal.Message);

        var job = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal(JobResult.Printer, job.Result);
        Assert.Equal(503, job.HttpStatus);
        Assert.Equal($"Printer not ready: {reason}", job.Error);
        Assert.Equal(StatusJson($"n1=16 n2=12 n4=12 n3={n3:x2}", n3, reason), job.PrinterStatus);
        Assert.True(job.ByteCount > 0);
    }

    [Fact]
    public async Task PostPrinter_IdleErrorStatus_SendsTheJob()
    {
        using var printer = new FakeStatusPrinter();
        await using var app = new LoopbackPrinterApp(printer.Port);

        var (status, _) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);

        Assert.Equal(HttpStatusCode.OK, status);
        var job = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal(job.Payload.Bytes, await printer.FirstJobAsync());
        Assert.Equal(StatusJson(IdleRaw), job.PrinterStatus);
    }

    // An unknown error status does not block a print.
    [Theory]
    [InlineData(null, "n3=?")]
    [InlineData(0xFF, "n3=ff!")]
    public async Task PostPrinter_ErrorStatusUnknown_SendsTheJob(int? n3, string rawPart)
    {
        using var printer = new FakeStatusPrinter { N3 = (byte?)n3 };
        await using var app = new LoopbackPrinterApp(printer.Port);

        var (status, _) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEmpty(await printer.FirstJobAsync());
        var job = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal(StatusJson($"n1=16 n2=12 n4=12 {rawPart}"), job.PrinterStatus);
    }

    [Fact]
    public async Task McpTools_CutterError_ShowTheFlagAndRefuseThePrint()
    {
        using var printer = new FakeStatusPrinter { N3 = 0x1A };
        using var app = new LoopbackPrinterApp(printer.Port);
        var client = app.CreateClient();

        var (_, status) = await client.CallToolAsync("get_status", "{}");
        var (_, print) = await client.CallToolAsync("print", PrintJson);
        var (_, printNote) = await client.CallToolAsync("print_note", """{"title":"T","message":"M"}""");

        Assert.Equal(StatusJson("n1=16 n2=12 n4=12 n3=1a", CutterBit, "cutter error"), status);
        Assert.Equal("Not printed: Printer not ready: cutter error", print);
        Assert.Equal("Not printed: Printer not ready: cutter error", printNote);
        Assert.Empty(printer.Jobs);
    }

    [Fact]
    public async Task McpGetStatus_Description_NamesTheErrorFlagsAsNotVerified()
    {
        using var app = new FakePrinterApp();

        var tools = (await app.CreateClient().McpAsync("tools/list")).GetProperty("tools");

        var description = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "get_status")
            .GetProperty("description").GetString();
        Assert.All(
            ["cutterError", "unrecoverableError", "autoRecoverableError", "recoverableError", "not verified on hardware", "notReadyReason"],
            text => Assert.Contains(text, description));
    }

    // The order of the causes: the one a person can act on comes first, "printer offline" last.
    [Theory]
    [InlineData(true, true, true, true, "cover open")]
    [InlineData(false, true, true, true, "paper out")]
    [InlineData(false, false, true, true, "cutter error")]
    [InlineData(false, false, false, true, "printer offline")]
    public void NotReadyReason_SeveralCauses_NamesTheMostSpecific(bool coverOpen, bool paperOut, bool cutterError, bool offline, string reason)
    {
        var status = new PrinterStatus(true, !offline, coverOpen, paperOut, false, CutterError: cutterError);

        Assert.False(status.Ready);
        Assert.Equal(reason, status.NotReadyReason);
    }
}
