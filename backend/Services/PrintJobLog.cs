using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Services;

// One line per print job, for both transports: who sent it and how it ended.
// The same call hands the job to the journal, so a new entry point gets both.
// Public on purpose: the MCP tools take it as an injected parameter.
public sealed class PrintJobLog(ILogger<PrintJobLog> logger, IHttpContextAccessor httpContext)
{
    internal const string HttpTransport = "http";
    internal const string ReprintTransport = "http:reprint";

    internal const int MaxSourceLength = 64;
    internal const int MaxUserAgentLength = 256;

    internal static string McpTransport(string tool) => $"mcp:{tool}";

    // The reason of a failure is logged where it happens; this line holds the kind only.
    // content and options are what went to the print path; null content when the request held no job.
    // No default values: an entry point that leaves them out would store a row with no blocks.
    public void Write(string transport, string? source, PrintResult result, List<PrintContent>? content, PrintOptions? options)
    {
        // The journal row of the request (PrintJournalMiddleware). Not a log: it holds the content.
        PrintJobTrace.Current?.Outcome = new PrintJobOutcome(transport, source, result, content, options);

        logger.LogInformation(
            "Print job: transport={Transport} source=\"{Source}\" userAgent=\"{UserAgent}\" result={Result}",
            transport,
            LogSafeText.Clean(source, MaxSourceLength),
            LogSafeText.Clean(httpContext.HttpContext?.Request.Headers.UserAgent.ToString(), MaxUserAgentLength),
            result.Failure?.ToString() ?? "Printed");
    }
}
