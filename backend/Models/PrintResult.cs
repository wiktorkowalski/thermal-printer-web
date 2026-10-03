namespace ThermalPrinterWeb.Models;

public enum PrintFailure
{
    Validation,
    Printer
}

// Private constructor: a failure always carries its kind, so it cannot be
// mistaken for a printer fault (503, which clients retry).
public sealed record PrintResult
{
    public static readonly PrintResult Ok = new(null, null);

    private PrintResult(string? error, PrintFailure? failure)
    {
        Error = error;
        Failure = failure;
    }

    public string? Error { get; }
    public PrintFailure? Failure { get; }
    public bool Success => Failure is null;

    public static PrintResult Invalid(string error) => new(error, PrintFailure.Validation);
    public static PrintResult PrinterFault(string error) => new(error, PrintFailure.Printer);
}
