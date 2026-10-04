namespace ThermalPrinterWeb.Models;

public enum PrintFailure
{
    Validation,
    Printer,
    Busy
}

// Private constructor: a failure always carries its kind, so it cannot be
// mistaken for a printer fault (503, which clients retry).
public sealed record PrintResult
{
    public static readonly PrintResult Ok = new(null, null);

    // Server load: the payload is fine and the printer is not asked. Fixed text, the queue facts stay in the log.
    public static readonly PrintResult Busy = new("Server busy: too many image jobs wait for a decode. Send the job again in a few seconds.", PrintFailure.Busy);

    // One reprint runs at a time. The stored job is fine and the printer is not asked.
    public static readonly PrintResult ReprintBusy = new("Server busy: another reprint runs. Send it again in a few seconds.", PrintFailure.Busy);

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
