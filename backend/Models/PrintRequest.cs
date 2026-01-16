namespace ThermalPrinterWeb.Models;

public class PrintRequest
{
    // Simple mode
    public string? Name { get; set; }
    public string? Message { get; set; }
    public string? ImageBase64 { get; set; }

    // Template mode
    public List<PrintContent>? Content { get; set; }

    // Shared
    public PrintOptions? Options { get; set; }
    public string? Source { get; set; }
}
