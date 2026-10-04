namespace ThermalPrinterWeb.Models;

public record PrintResponse(bool Success, string? Error = null, string? Type = null)
{
    public const string ValidationType = "validation";
    public const string PrinterType = "printer";
    public const string BusyType = "busy";
    // Only the job endpoints answer with these two.
    // The print journal cannot be read at the moment (not open yet, or a read fault): a later call can pass.
    public const string JournalType = "journal";
    // The print journal is off: no later call passes until the settings change.
    public const string JournalOffType = "journal-off";
}
