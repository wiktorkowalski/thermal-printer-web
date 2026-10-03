using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;

namespace ThermalPrinterWeb.Tests;

// The real PrinterService against loopback ports: no test reaches the printer.
public sealed class PrinterFaultTests
{
    private const string Loopback = "127.0.0.1";
    private const string UnreachableStatusJson =
        """{"reachable":false,"online":false,"coverOpen":false,"paperOut":false,"paperLow":false,"raw":null,"ready":false,"notReadyReason":"printer unreachable"}""";
    private const string PrintJson = """{"content":[{"type":"Text","content":"x"}]}""";

    private static readonly string ServiceCategory = typeof(PrinterService).FullName!;

    // The app with PrinterService pointed at a loopback port.
    private sealed class App(int port, TimeSpan? connectTimeout = null) : WebApplicationFactory<Program>
    {
        public McpToolTests.RecordingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPrinterService>();
                services.AddSingleton<IPrinterService>(provider => new PrinterService(
                    provider.GetRequiredService<ILogger<PrinterService>>(),
                    provider.GetServices<IBlockHandler>())
                {
                    PrinterAddress = $"{Loopback}:{port}",
                    ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3)
                });
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
            });
        }

        public List<(LogLevel Level, string Category, string Message)> ServiceLogs
            => [.. Logs.Entries.Where(entry => entry.Category == ServiceCategory && entry.Level >= LogLevel.Information)];
    }

    // A port with no listener: the connection is refused.
    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // Answers one status read as a ready printer, then stops: the connection for the job is refused.
    private static (int Port, Task Done) ReadyThenGone()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var done = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var query = new byte[3];
            for (var i = 0; i < 3; i++)
            {
                await stream.ReadExactlyAsync(query);
                if (i == 2)
                    listener.Dispose();
                // Online, cover closed, paper present.
                await stream.WriteAsync(new byte[] { 0x12 });
            }
        });
        return (port, done);
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, HttpMethod method, string url, string? json = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        // Streamable HTTP (MCP) needs both; the controller ignores them.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> CallToolAsync(HttpClient client, string tool, string argumentsJson)
    {
        var rpc = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"TOOL","arguments":ARGUMENTS}}"""
            .Replace("TOOL", tool)
            .Replace("ARGUMENTS", argumentsJson);
        var (_, body) = await SendAsync(client, HttpMethod.Post, "/mcp", rpc);

        var data = body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        return JsonDocument.Parse(data).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    // What an exception message from the socket holds.
    private static void AssertNoFaultDetails(string text, int port)
    {
        Assert.DoesNotContain(Loopback, text);
        Assert.DoesNotContain(port.ToString(), text);
        Assert.DoesNotContain("refused", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("canceled", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStatus_ConnectionRefused_Returns503WithoutExceptionText()
    {
        var port = ClosedPort();
        using var app = new App(port);

        var (status, body) = await SendAsync(app.CreateClient(), HttpMethod.Get, "/api/printer/status");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(UnreachableStatusJson, body);
        // The exception, with the address, is in the server log one time.
        var entry = Assert.Single(app.ServiceLogs);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(SocketException), entry.Message);
        Assert.Contains($"{Loopback}:{port}", entry.Message);
    }

    [Fact]
    public async Task GetStatus_ConnectTimeout_Returns503WithoutExceptionText()
    {
        // A zero timeout cancels the connect the same way a slow network does.
        using var app = new App(ClosedPort(), TimeSpan.Zero);

        var (status, body) = await SendAsync(app.CreateClient(), HttpMethod.Get, "/api/printer/status");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(UnreachableStatusJson, body);
        Assert.Contains(nameof(TaskCanceledException), Assert.Single(app.ServiceLogs).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostPrinter_PrinterUnreachable_Returns503WithFixedText(bool timeout)
    {
        var port = ClosedPort();
        using var app = new App(port, timeout ? TimeSpan.Zero : null);

        var (status, body) = await SendAsync(app.CreateClient(), HttpMethod.Post, "/api/printer", PrintJson);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("""{"success":false,"error":"Printer not ready: printer unreachable","type":"printer"}""", body);
        // One line for the fault: the status read logs it, the print does not log it again.
        Assert.Equal(LogLevel.Warning, Assert.Single(app.ServiceLogs).Level);
    }

    [Fact]
    public async Task PostPrinter_ConnectionLostAfterTheStatusRead_Returns503WithFixedText()
    {
        var (port, printerDone) = ReadyThenGone();
        using var app = new App(port);

        var (status, body) = await SendAsync(app.CreateClient(), HttpMethod.Post, "/api/printer", PrintJson);
        await printerDone;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("""{"success":false,"error":"Printer unreachable","type":"printer"}""", body);
        var entry = Assert.Single(app.ServiceLogs);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.StartsWith($"Print failed: no connection to the printer at {Loopback}:{port}", entry.Message);
        Assert.Contains("Exception", entry.Message);
    }

    [Fact]
    public async Task McpTools_PrinterUnreachable_ShowTheSameFixedText()
    {
        var port = ClosedPort();
        using var app = new App(port);
        var client = app.CreateClient();

        var print = await CallToolAsync(client, "print", PrintJson);
        var printNote = await CallToolAsync(client, "print_note", """{"title":"T","message":"M"}""");
        var status = await CallToolAsync(client, "get_status", "{}");

        Assert.Equal("Not printed: Printer not ready: printer unreachable", print);
        Assert.Equal("Not printed: Printer not ready: printer unreachable", printNote);
        // The MCP serializer leaves out null values.
        Assert.Equal(UnreachableStatusJson.Replace("\"raw\":null,", ""), status);
        Assert.All([print, printNote, status], text => AssertNoFaultDetails(text, port));
    }

    [Fact]
    public async Task McpPrint_ConnectionLostAfterTheStatusRead_ShowsTheFixedText()
    {
        var (port, printerDone) = ReadyThenGone();
        using var app = new App(port);

        var text = await CallToolAsync(app.CreateClient(), "print", PrintJson);
        await printerDone;

        Assert.Equal("Not printed: Printer unreachable", text);
    }

    // A fault in the service itself: the caller gets no exception text either.
    [Fact]
    public async Task PrintAsync_UnexpectedException_ReturnsFixedTextAndLogsTheExceptionOnce()
    {
        var logs = new McpToolTests.RecordingLoggerProvider();
        var service = new PrinterService(new LoggerFactory([logs]).CreateLogger<PrinterService>(), []);

        var result = await service.PrintAsync(null!);

        Assert.Equal(PrintResult.PrinterFault("Print failed: internal error"), result);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(nameof(NullReferenceException), entry.Message);
    }
}
