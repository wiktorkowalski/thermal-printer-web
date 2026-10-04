using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;

namespace ThermalPrinterWeb.Tests;

// The real PrinterService against loopback ports: no test reaches the printer.
public sealed class PrinterFaultTests
{
    private const string Loopback = TestApp.Loopback;
    private const string UnreachableStatusJson =
        """{"reachable":false,"online":false,"coverOpen":false,"paperOut":false,"paperLow":false,"raw":null,"ready":false,"notReadyReason":"printer unreachable"}""";
    private const string PrintJson = TestHttp.PrintJson;

    private static readonly byte[] ReadyStatus = [WirePrinter.ReadyStatus];

    private static readonly string ServiceCategory = typeof(PrinterService).FullName!;

    private static List<(LogLevel Level, string Category, string Message)> ServiceLogs(TestApp app)
        => [.. app.Logs.Entries.Where(entry => entry.Category == ServiceCategory && entry.Level >= LogLevel.Information)];

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
                await stream.WriteAsync(ReadyStatus);
            }
        });
        return (port, done);
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
        using var app = new LoopbackPrinterApp(port);

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Get, "/api/printer/status");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(UnreachableStatusJson, body);
        // The exception, with the address, is in the server log one time.
        var entry = Assert.Single(ServiceLogs(app));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(SocketException), entry.Message);
        Assert.Contains($"{Loopback}:{port}", entry.Message);
    }

    [Fact]
    public async Task GetStatus_ConnectTimeout_Returns503WithoutExceptionText()
    {
        // A zero timeout cancels the connect the same way a slow network does.
        using var app = new LoopbackPrinterApp(ClosedPort(), TimeSpan.Zero);

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Get, "/api/printer/status");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(UnreachableStatusJson, body);
        Assert.Contains(nameof(TaskCanceledException), Assert.Single(ServiceLogs(app)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostPrinter_PrinterUnreachable_Returns503WithFixedText(bool timeout)
    {
        var port = ClosedPort();
        using var app = new LoopbackPrinterApp(port, timeout ? TimeSpan.Zero : null);

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, "/api/printer", PrintJson);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("""{"success":false,"error":"Printer not ready: printer unreachable","type":"printer"}""", body);
        // One line for the fault: the status read logs it, the print does not log it again.
        Assert.Equal(LogLevel.Warning, Assert.Single(ServiceLogs(app)).Level);
    }

    [Fact]
    public async Task PostPrinter_ConnectionLostAfterTheStatusRead_Returns503WithFixedText()
    {
        var (port, printerDone) = ReadyThenGone();
        using var app = new LoopbackPrinterApp(port);

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, "/api/printer", PrintJson);
        await printerDone;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("""{"success":false,"error":"Printer unreachable","type":"printer"}""", body);
        var entry = Assert.Single(ServiceLogs(app));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.StartsWith($"Print failed: no connection to the printer at {Loopback}:{port}", entry.Message);
        Assert.Contains("Exception", entry.Message);
    }

    [Fact]
    public async Task McpTools_PrinterUnreachable_ShowTheSameFixedText()
    {
        var port = ClosedPort();
        using var app = new LoopbackPrinterApp(port);
        var client = app.CreateClient();

        var (_, print) = await client.CallToolAsync("print", PrintJson);
        var (_, printNote) = await client.CallToolAsync("print_note", """{"title":"T","message":"M"}""");
        var (_, status) = await client.CallToolAsync("get_status", "{}");

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
        using var app = new LoopbackPrinterApp(port);

        var (_, text) = await app.CreateClient().CallToolAsync("print", PrintJson);
        await printerDone;

        Assert.Equal("Not printed: Printer unreachable", text);
    }

    // A fault in the service itself: the caller gets no exception text either.
    [Fact]
    public async Task PrintAsync_UnexpectedException_ReturnsFixedTextAndLogsTheExceptionOnce()
    {
        var logs = new RecordingLogger<PrinterService>();
        var service = new PrinterService(logs, [], NoPrinter.Options);

        var result = await service.PrintAsync(null!);

        Assert.Equal(PrintResult.PrinterFault("Print failed: internal error"), result);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(nameof(NullReferenceException), entry.Message);
    }
}
