namespace ThermalPrinterWeb.Models;

public record PrintResponse(bool Success, string? Error = null, string? Type = null);
