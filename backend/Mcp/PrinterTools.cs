using System.ComponentModel;
using ModelContextProtocol.Server;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;

namespace ThermalPrinterWeb.Mcp;

// Public on purpose: WithToolsFromAssembly() discovers tools by reflecting over
// public [McpServerToolType] classes / [McpServerTool] methods. IPrinterService
// is injected from the request's DI scope; the remaining parameters form each
// tool's input schema.
[McpServerToolType]
public static class PrinterTools
{
    [McpServerTool(Name = "get_status")]
    [Description("Read the thermal printer's live status (reachable, online, cover open, paper out). Call before printing so a job is not rejected.")]
    public static async Task<PrinterStatus> GetStatusAsync(IPrinterService printer)
        => await printer.GetStatusAsync();

    [McpServerTool(Name = "print_note")]
    [Description("Print a simple note: a large centered title, a message below it, an optional image, then a cut. Best for quick notes and messages.")]
    public static async Task<string> PrintNoteAsync(
        IPrinterService printer,
        [Description("Title, printed large and centered at the top.")] string title,
        [Description("Message body, printed centered under the title.")] string message,
        [Description("Optional base64-encoded PNG to print under the message.")] string? imageBase64 = null)
    {
        var result = await printer.PrintAsync(SimpleNote.Build(title, message, imageBase64));
        return result.Success ? "Printed." : $"Not printed: {result.Error}";
    }

    [McpServerTool(Name = "beep")]
    [Description("Sound the printer's buzzer without printing - an audible way to get Wiktor's attention. count = number of beeps (1-9), duration = length of each (1-9).")]
    public static async Task<string> BeepAsync(
        IPrinterService printer,
        [Description("Number of beeps, 1-9.")] int count = 1,
        [Description("Duration of each beep, 1-9.")] int duration = 1)
    {
        var ok = await printer.BeepAsync(count, duration);
        return ok ? $"Beeped {count}x." : "Beep failed: printer unreachable.";
    }

    [McpServerTool(Name = "print")]
    [Description("Print a custom document: an ordered list of content blocks (Text, Image, Barcode, QRCode, LineFeed, Cut, Separator, CodePage). Use for full control over styling, barcodes, QR codes and images.")]
    public static async Task<string> PrintAsync(
        IPrinterService printer,
        [Description("Ordered content blocks to print, top to bottom.")] List<PrintContent> content,
        [Description("Optional print options: code page, line spacing, auto-cut.")] PrintOptions? options = null)
    {
        var result = await printer.PrintAsync(content, options);
        return result.Success ? "Printed." : $"Not printed: {result.Error}";
    }
}
