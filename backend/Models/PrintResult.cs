namespace ThermalPrinterWeb.Models;

// Why a job did not print: the caller's payload, or the printer / connection.
public enum PrintFailure
{
    Validation,
    Printer
}

public record PrintResult(bool Success, string? Error = null, PrintFailure? Failure = null)
{
    public static PrintResult Invalid(string error) => new(false, error, PrintFailure.Validation);
    public static PrintResult PrinterFault(string error) => new(false, error, PrintFailure.Printer);
}
