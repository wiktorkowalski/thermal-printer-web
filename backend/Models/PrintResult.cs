namespace ThermalPrinterWeb.Models;

// Why a job did not print: the caller's payload, or the printer / connection.
public enum PrintFailure
{
    Validation,
    Printer
}

public record PrintResult(string? Error = null, PrintFailure? Failure = null)
{
    public static readonly PrintResult Ok = new();

    public bool Success => Failure is null;

    public static PrintResult Invalid(string error) => new(error, PrintFailure.Validation);
    public static PrintResult PrinterFault(string error) => new(error, PrintFailure.Printer);
}
