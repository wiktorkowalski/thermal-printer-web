using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services;

// One line per print job, for both transports: who sent it and how it ended.
// Public on purpose: the MCP tools take it as an injected parameter.
public sealed class PrintJobLog(ILogger<PrintJobLog> logger, IHttpContextAccessor httpContext)
{
    internal const string HttpTransport = "http";

    internal const int MaxSourceLength = 64;
    internal const int MaxUserAgentLength = 256;

    internal static string McpTransport(string tool) => $"mcp:{tool}";

    // The reason of a failure is logged where it happens; this line holds the kind only.
    public void Write(string transport, string? source, PrintResult result)
        => logger.LogInformation(
            "Print job: transport={Transport} source=\"{Source}\" userAgent=\"{UserAgent}\" result={Result}",
            transport,
            LogSafeText.Clean(source, MaxSourceLength),
            LogSafeText.Clean(httpContext.HttpContext?.Request.Headers.UserAgent.ToString(), MaxUserAgentLength),
            result.Failure?.ToString() ?? "Printed");
}
