namespace ThermalPrinterWeb.Models;

public class PrintRequest
{
    // Simple mode
    public string? Name { get; set; }
    public string? Message { get; set; }
    public string? ImageBase64 { get; set; }

    // Template mode
    public List<PrintContent>? Content { get; set; }

    // Text mode (StripMarkup): plain text with line markers. It goes alone: with content, name, message or imageBase64 the request is a 400.
    public string? Text { get; set; }
    // Text mode only: Left, Center or Right for the body lines, letter case aside. Text, not the enum: the enum binder takes a number with no name.
    public string? Align { get; set; }

    // Shared
    public PrintOptions? Options { get; set; }
    public string? Source { get; set; }
}
