using System.Globalization;
using System.Text;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services;

// One line per print job, for both transports: who sent it and how it ended.
// Public on purpose: the MCP tools take it as an injected parameter.
public sealed class PrintJobLog(ILogger<PrintJobLog> logger, IHttpContextAccessor httpContext)
{
    public const string HttpTransport = "http";

    internal const int MaxSourceLength = 64;
    internal const int MaxUserAgentLength = 256;
    internal const string Missing = "-";

    private const char Replacement = '?';

    public static string McpTransport(string tool) => $"mcp:{tool}";

    // The reason of a failure is logged where it happens; this line holds the kind only.
    public void Write(string transport, string? source, PrintResult result)
        => Write(transport, source, result.Failure?.ToString() ?? "Printed");

    public void WriteRejected(string transport, string? source)
        => Write(transport, source, nameof(PrintFailure.Validation));

    private void Write(string transport, string? source, string result)
        => logger.LogInformation(
            "Print job: transport={Transport} source=\"{Source}\" userAgent=\"{UserAgent}\" result={Result}",
            transport,
            Clean(source, MaxSourceLength),
            Clean(httpContext.HttpContext?.Request.Headers.UserAgent.ToString(), MaxUserAgentLength),
            result);

    // Caller text: no character may start a new log line, hide text or close the quotes around the value.
    internal static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Missing;

        var cleaned = new StringBuilder(Math.Min(value.Length, maxLength));
        // Runes: a cut never splits a surrogate pair, and a lone surrogate becomes U+FFFD.
        foreach (var rune in value.AsSpan().Trim().EnumerateRunes())
        {
            if (cleaned.Length + rune.Utf16SequenceLength > maxLength)
                break;

            if (rune.Value == '"')
                cleaned.Append('\'');
            else if (IsUnsafe(rune))
                cleaned.Append(Replacement);
            else
                cleaned.Append(rune.ToString());
        }

        return cleaned.ToString();
    }

    private static bool IsUnsafe(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.Control            // C0, DEL, C1: CR, LF, ESC
        or UnicodeCategory.Format          // bidi overrides, zero-width characters
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator
        or UnicodeCategory.PrivateUse
        or UnicodeCategory.OtherNotAssigned;
}
