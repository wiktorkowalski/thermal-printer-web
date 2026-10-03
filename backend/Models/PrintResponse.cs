namespace ThermalPrinterWeb.Models;

public record PrintResponse(bool Success, string? Error = null, string? Type = null)
{
    public const string ValidationType = "validation";
    public const string PrinterType = "printer";
}
