using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Tests;

// The bearer token of /mcp, through the real pipeline: who gets in, and where the key must never show up.
public sealed class McpAuthTests
{
    private const string Key = TestApp.McpKey;
    private const string WrongKey = "WRONG-KEY-0c4d1f7a9e";
    private const string PrintUrl = "/api/printer";
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";
    private const string PrintCall =
        """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"print","arguments":{"content":[{"type":"Text","content":"x"}]}}}""";

    private static readonly string AuthCategory = typeof(McpApiKeyMiddleware).FullName!;

    private sealed class App(string environment = "Production") : TestApp(environment)
    {
        public RecordingPrinter Printer { get; } = new();

        protected override void ConfigurePrinter(IWebHostBuilder builder) => UseRecordingPrinter(builder, Printer);

        public List<(LogLevel Level, string Category, string Message)> AuthLogs()
            => [.. Logs.Entries.Where(entry => entry.Category == AuthCategory)];
    }

    private static string Basic(string key) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:" + key));

    public static TheoryData<string?> RejectedHeaders() => new()
    {
        { null },
        { "" },
        { "Bearer" },
        { "Bearer " },
        { "Bearer " + WrongKey },
        // The key with no scheme, and with another scheme.
        { Key },
        { Basic(Key) },
        { "Token " + Key },
        // Close to the key: one character more, one less, other letter case.
        { "Bearer " + Key + "x" },
        { "Bearer " + Key[..^1] },
        { "Bearer " + Key.ToUpperInvariant() },
        { "Bearer " + Key + ", Bearer " + Key }
    };

    [Theory]
    [MemberData(nameof(RejectedHeaders))]
    public async Task Mcp_WithoutTheRightBearerToken_Is401AndReachesNoTool(string? authorization)
    {
        await using var app = new App();
        var client = app.CreateClient();
        await app.JournalIdleAsync();
        app.Logs.Entries.Clear();

        using var request = new HttpRequestMessage(HttpMethod.Post, TestHttp.McpUrl) { Content = TestHttp.Json(PrintCall) };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (authorization is not null)
            Assert.True(request.Headers.TryAddWithoutValidation("Authorization", authorization));
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(McpApiKeyMiddleware.Unauthorized, body);
        // An MCP client reads this header as a prompt to start OAuth discovery.
        Assert.False(response.Headers.Contains("WWW-Authenticate"));
        Assert.Empty(app.Printer.Jobs);
        Assert.Empty(await app.JournalRowsAsync());

        // One Warning, and no log line holds the key or the value of the header.
        var (level, _, message) = Assert.Single(app.AuthLogs());
        Assert.Equal(LogLevel.Warning, level);
        Assert.StartsWith("MCP auth failed: POST /mcp from ", message);
        Assert.Single(app.Logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.All(app.Logs.Entries, entry =>
        {
            Assert.DoesNotContain(Key, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(WrongKey, entry.Message);
            Assert.DoesNotContain("Bearer", entry.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("POST", "/mcp/")]
    [InlineData("POST", "/MCP")]
    [InlineData("POST", "/Mcp/")]
    [InlineData("POST", "/%6Dcp")]
    [InlineData("POST", "/%4Dcp")]
    [InlineData("POST", "/mcp/sse")]
    [InlineData("POST", "/mcp/message")]
    [InlineData("POST", "/mcp/anything/below")]
    [InlineData("POST", "/mcp?x=1")]
    [InlineData("GET", "/mcp")]
    [InlineData("GET", "/mcp/sse")]
    [InlineData("DELETE", "/mcp")]
    [InlineData("PUT", "/mcp")]
    [InlineData("OPTIONS", "/mcp")]
    [InlineData("HEAD", "/mcp")]
    public async Task Mcp_AnyPathVariantOrMethodWithoutTheToken_Is401(string method, string url)
    {
        await using var app = new App();
        var client = app.CreateClient();

        var (status, _) = await client.SendJsonAsync(new HttpMethod(method), url, method == "POST" ? PrintCall : null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Empty(app.Printer.Jobs);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Key));
    }

    [Theory]
    [InlineData("Bearer " + Key)]
    [InlineData("bearer " + Key)]
    [InlineData("BEARER " + Key)]
    public async Task Mcp_WithTheToken_Answers(string authorization)
    {
        await using var app = new App();
        var client = app.CreateClient();
        app.Logs.Entries.Clear();

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, Initialize, authorization: authorization);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"instructions\"", body);
        Assert.Empty(app.AuthLogs());
    }

    // A space or a line end around the key in an env file is not part of the key.
    [Fact]
    public async Task Mcp_KeySettingWithSpaceAround_IsTheKeyWithoutIt()
    {
        await using var app = new App { McpApiKey = $"  {Key}\r\n" };
        var client = app.CreateClient();

        var (withKey, _) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, Initialize, authorization: TestHttp.McpAuthorization);
        var (without, _) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, Initialize);

        Assert.Equal(HttpStatusCode.OK, withKey);
        Assert.Equal(HttpStatusCode.Unauthorized, without);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Mcp_NoKeyOutsideDevelopment_RejectsEveryCallAndTheHttpApiStaysUp(string setting)
    {
        await using var app = new App { McpApiKey = setting };
        var client = app.CreateClient();

        var (noHeader, _) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, Initialize);
        var (emptyBearer, _) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, Initialize, authorization: "Bearer ");
        var (spaceBearer, _) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, Initialize, authorization: "Bearer " + setting);
        var (anyBearer, _) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, Initialize, authorization: TestHttp.McpAuthorization);
        var (print, _) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, TestHttp.PrintJson);
        // The journal is open: a read before that answers 503.
        await app.JournalIdleAsync();
        var (list, _) = await client.SendJsonAsync(HttpMethod.Get, PrintUrl + "/jobs");

        Assert.All([noHeader, emptyBearer, spaceBearer, anyBearer], code => Assert.Equal(HttpStatusCode.Unauthorized, code));
        Assert.Equal(HttpStatusCode.OK, print);
        Assert.Equal(HttpStatusCode.OK, list);
        Assert.Single(app.Printer.Jobs);

        // One Warning when the host starts, then one per rejected call.
        var warnings = app.AuthLogs();
        Assert.Equal(5, warnings.Count);
        Assert.All(warnings, warning => Assert.Equal(LogLevel.Warning, warning.Level));
        Assert.Equal("MCP API key is not set: /mcp rejects every request. Set McpServer:ApiKey", warnings[0].Message);
    }

    [Fact]
    public async Task Mcp_NoKeyInDevelopment_IsOpen()
    {
        await using var app = new App("Development") { McpApiKey = "" };
        var client = app.CreateClient();

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, Initialize);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"instructions\"", body);
        var (level, _, message) = Assert.Single(app.AuthLogs());
        Assert.Equal(LogLevel.Information, level);
        Assert.Equal("MCP API key is not set: /mcp is open in Development", message);
    }

    [Fact]
    public async Task Mcp_KeySetInDevelopment_NeedsTheToken()
    {
        await using var app = new App("Development");
        var client = app.CreateClient();

        var (without, _) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, Initialize);
        var (withKey, _) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, Initialize, authorization: TestHttp.McpAuthorization);

        Assert.Equal(HttpStatusCode.Unauthorized, without);
        Assert.Equal(HttpStatusCode.OK, withKey);
    }

    // Owner decision: print, reprint and the journal reads over HTTP stay open.
    [Fact]
    public async Task HttpApi_WithoutAToken_StaysOpen()
    {
        await using var app = new App();
        var client = app.CreateClient();

        var (print, _) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, TestHttp.PrintJson);
        var job = Assert.Single(await app.JournalRowsAsync());
        var (list, _) = await client.SendJsonAsync(HttpMethod.Get, PrintUrl + "/jobs");
        var (one, _) = await client.SendJsonAsync(HttpMethod.Get, $"{PrintUrl}/jobs/{job.Id}");
        var (stats, _) = await client.SendJsonAsync(HttpMethod.Get, PrintUrl + "/jobs/stats");
        var (reprint, _) = await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{job.Id}/reprint");

        Assert.All([print, list, one, stats, reprint], code => Assert.Equal(HttpStatusCode.OK, code));
        Assert.Empty(app.AuthLogs());
    }

    // The journal stores the request with its headers, and the log lines of the request. The key must be in none of them.
    [Fact]
    public async Task Key_AfterAcceptedAndRejectedCalls_IsInNoJournalRowNoLogAndNoAnswer()
    {
        await using var app = new App();
        var client = app.CreateClient();

        // Accepted: a print, and a call that the argument filter rejects (also a journal row).
        var (isError, printed) = await client.CallToolAsync("print", TestHttp.PrintJson);
        var (_, wrongShape) = await client.CallToolAsync("print", """{"text":"x"}""");
        // A client that sends the key to the open HTTP API too.
        var (http, httpBody) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, TestHttp.PrintJson, authorization: TestHttp.McpAuthorization);
        // Rejected: a wrong key, and the right key in the wrong form.
        var (wrong, wrongBody) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, PrintCall, authorization: "Bearer " + WrongKey);
        var (raw, rawBody) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, PrintCall, authorization: Key);
        var (basic, basicBody) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, PrintCall, authorization: Basic(Key));

        Assert.False(isError, printed);
        Assert.Equal(HttpStatusCode.OK, http);
        Assert.All([wrong, raw, basic], code => Assert.Equal(HttpStatusCode.Unauthorized, code));

        // A rejected call is not a job: three rows, for the three accepted calls.
        var rows = await app.JournalRowsAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, app.Printer.Jobs.Count);
        Assert.All(rows, row => Assert.Contains($"\"Authorization\":[\"{PrintJournalMiddleware.Redacted}\"]", row.Payload.Headers));

        var basicValue = Basic(Key)["Basic ".Length..];
        string?[] secrets = [Key, WrongKey, basicValue];
        foreach (var row in rows)
        {
            string?[] stored =
            [
                row.Transport, row.Source, row.UserAgent, row.RemoteIp, row.Error, row.Title, row.PrinterStatus, row.AppVersion,
                row.Payload.Headers, row.Payload.Blocks, row.Payload.Options, row.Payload.PlainText, row.Payload.Exception, row.Payload.Log,
                row.Payload.Request is null ? null : Encoding.UTF8.GetString(row.Payload.Request),
                row.Payload.Bytes is null ? null : Encoding.Latin1.GetString(row.Payload.Bytes)
            ];
            Assert.All(stored, value => Assert.All(secrets, secret => Assert.DoesNotContain(secret!, value ?? "", StringComparison.OrdinalIgnoreCase)));
        }

        // The database files themselves: a column that the list above does not name would show here.
        foreach (var file in Directory.GetFiles(app.JournalDirectory))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            var text = Encoding.Latin1.GetString(copy.ToArray());
            Assert.All(secrets, secret => Assert.DoesNotContain(secret!, text));
        }

        Assert.All([printed, wrongShape, httpBody, wrongBody, rawBody, basicBody], answer =>
            Assert.All(secrets, secret => Assert.DoesNotContain(secret!, answer, StringComparison.OrdinalIgnoreCase)));
        Assert.All(app.Logs.Entries, entry =>
            Assert.All(secrets, secret => Assert.DoesNotContain(secret!, entry.Message, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(3, app.AuthLogs().Count(entry => entry.Level == LogLevel.Warning));
    }
}
