using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;

namespace ThermalPrinterWeb.Tests;

// Both transports through the real pipeline, with the printer replaced: no test reaches the network.
public sealed class PrintJobLogTests(McpToolTests.McpApp app) : IClassFixture<McpToolTests.McpApp>
{
    private const string Secret = "SECRET-CALLER-CONTENT";
    private const string HttpSimple = "http-simple";
    private const string HttpTemplate = "http-template";
    private const string McpPrint = "mcp:print";
    private const string McpPrintNote = "mcp:print_note";

    private static readonly string JobLogCategory = typeof(PrintJobLog).FullName!;

    private readonly HttpClient _client = app.CreateClient();

    public static TheoryData<string, string> Paths() => new()
    {
        { HttpSimple, "http" },
        { HttpTemplate, "http" },
        { McpPrint, McpPrint },
        { McpPrintNote, McpPrintNote }
    };

    private async Task<List<(LogLevel Level, string Category, string Message)>> SendAsync(string url, string json, string? userAgent = null)
    {
        app.Logs.Entries.Clear();

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        // Streamable HTTP (MCP) needs both; the controller ignores them.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (userAgent is not null)
            Assert.True(request.Headers.TryAddWithoutValidation("User-Agent", userAgent));

        using var response = await _client.SendAsync(request);
        await response.Content.ReadAsStringAsync();

        return [.. app.Logs.Entries.Where(entry => entry.Category == JobLogCategory)];
    }

    // Sends one print job that holds Secret as its content and returns the job lines it logged.
    private Task<List<(LogLevel Level, string Category, string Message)>> PrintAsync(string path, string? source, string? userAgent)
    {
        var arguments = new Dictionary<string, object?>();
        if (path is HttpSimple or McpPrintNote)
        {
            arguments[path == HttpSimple ? "name" : "title"] = Secret;
            arguments["message"] = Secret;
        }
        else
        {
            arguments["content"] = new[] { new { type = "Text", content = Secret } };
        }

        if (source is not null)
            arguments["source"] = source;

        var isMcp = path is McpPrint or McpPrintNote;
        object body = isMcp
            ? new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = path["mcp:".Length..], arguments } }
            : arguments;

        return SendAsync(isMcp ? "/mcp" : "/api/printer", JsonSerializer.Serialize(body), userAgent);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Print_KnownCaller_LogsOneJobLineWithEveryField(string path, string transport)
    {
        var jobLines = await PrintAsync(path, "claude-code", "test-agent/1.0 (unit)");

        var line = Assert.Single(jobLines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal($"Print job: transport={transport} source=\"claude-code\" userAgent=\"test-agent/1.0 (unit)\" result=Printed", line.Message);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Print_NoSourceAndNoUserAgent_LogsPlaceholders(string path, string transport)
    {
        var jobLines = await PrintAsync(path, source: null, userAgent: null);

        Assert.Equal($"Print job: transport={transport} source=\"-\" userAgent=\"-\" result=Printed", Assert.Single(jobLines).Message);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Print_HostileSource_LogsOneCleanLine(string path, string transport)
    {
        var jobLines = await PrintAsync(path, "evil\r\nPrint job: transport=http source=\"x\"\u001b[31m\u2028\u202E", "ua");

        Assert.Equal(
            $"Print job: transport={transport} source=\"evil??Print job: transport=http source='x'?[31m??\" userAgent=\"ua\" result=Printed",
            Assert.Single(jobLines).Message);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Print_OverlongSourceAndUserAgent_AreCut(string path, string transport)
    {
        var jobLines = await PrintAsync(path, new string('s', 10_240), new string('u', 10_240));

        Assert.Equal(
            $"Print job: transport={transport} source=\"{new string('s', PrintJobLog.MaxSourceLength)}\" userAgent=\"{new string('u', PrintJobLog.MaxUserAgentLength)}\" result=Printed",
            Assert.Single(jobLines).Message);
    }

    // A header cannot hold CR or LF; ESC and a quote can arrive.
    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Print_HostileUserAgent_LogsOneCleanLine(string path, string transport)
    {
        var jobLines = await PrintAsync(path, "s", "ua\u001b[31m\" result=\"Printed");

        Assert.Equal(
            $"Print job: transport={transport} source=\"s\" userAgent=\"ua?[31m' result='Printed\" result=Printed",
            Assert.Single(jobLines).Message);
    }

    [Theory]
    [InlineData(HttpTemplate, "http", PrintFailure.Validation)]
    [InlineData(HttpTemplate, "http", PrintFailure.Printer)]
    [InlineData(McpPrint, McpPrint, PrintFailure.Validation)]
    [InlineData(McpPrintNote, McpPrintNote, PrintFailure.Printer)]
    [InlineData(HttpTemplate, "http", PrintFailure.Busy)]
    [InlineData(McpPrint, McpPrint, PrintFailure.Busy)]
    public async Task Print_FailedJob_LogsTheKindWithoutTheReason(string path, string transport, PrintFailure failure)
    {
        app.Printer.Result = failure switch
        {
            PrintFailure.Validation => PrintResult.Invalid(Secret),
            PrintFailure.Busy => PrintResult.Busy,
            _ => PrintResult.PrinterFault(Secret)
        };
        try
        {
            var jobLines = await PrintAsync(path, "claude-code", "ua");

            Assert.Equal(
                $"Print job: transport={transport} source=\"claude-code\" userAgent=\"ua\" result={failure}",
                Assert.Single(jobLines).Message);
        }
        finally
        {
            app.Printer.Result = PrintResult.Ok;
        }
    }

    [Fact]
    public async Task PostPrinter_NoContentAndNoMessage_LogsOneRejectedJobLine()
    {
        app.Printer.Jobs.Clear();
        var jobLines = await SendAsync("/api/printer", """{"source":"claude-code"}""");

        Assert.Equal(
            "Print job: transport=http source=\"claude-code\" userAgent=\"-\" result=Validation",
            Assert.Single(jobLines).Message);
        Assert.Empty(app.Printer.Jobs);
    }

    // A wrong-shaped MCP call is not a job: ArgumentShapeFilter logs it.
    [Fact]
    public async Task ToolsCall_WrongShape_LogsNoJobLine()
    {
        var jobLines = await SendAsync(
            "/mcp",
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"print","arguments":{"source":"claude-code"}}}""");

        Assert.Empty(jobLines);
        Assert.Single(app.Logs.Entries, entry => entry.Message == "Rejected MCP call to print: 'content' is missing");
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData("", "-")]
    [InlineData(" \t\r\n ", "-")]
    [InlineData("  claude-code  ", "claude-code")]
    [InlineData("web/receipt", "web/receipt")]
    [InlineData("zażółć", "zażółć")]
    [InlineData("a\rb\nc\td", "a?b?c?d")]
    [InlineData("a\u0000b\u001bc\u007fd\u0085e", "a?b?c?d?e")]
    [InlineData("a\u2028b\u2029c", "a?b?c")]
    [InlineData("a\u202Eb\u200Bc\uFEFFd", "a?b?c?d")]
    [InlineData("say \"hi\"", "say 'hi'")]
    public void Clean_CallerText_IsSafeForOneLogLine(string? value, string expected)
        => Assert.Equal(expected, LogSafeText.Clean(value, PrintJobLog.MaxSourceLength));

    [Fact]
    public void Clean_Cut_NeverSplitsASurrogatePair()
    {
        var cleaned = LogSafeText.Clean("abc\U0001F600", 4);

        Assert.Equal("abc", cleaned);
    }

    [Fact]
    public void Clean_LoneSurrogate_IsReplaced()
        => Assert.Equal("a\uFFFDb", LogSafeText.Clean("a\uD83Db", PrintJobLog.MaxSourceLength));
}
