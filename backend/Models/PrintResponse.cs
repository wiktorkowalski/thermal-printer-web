namespace ThermalPrinterWeb.Models;

public record PrintResponse(bool Success, string? Error = null, string? Type = null)
{
    public const string ValidationType = "validation";
    public const string PrinterType = "printer";
    public const string BusyType = "busy";
    // The print journal is off or cannot be read. Only the job endpoints answer with it.
    public const string JournalType = "journal";
}
