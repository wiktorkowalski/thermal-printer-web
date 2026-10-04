using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Journal;

// How a reprint call ended: a journal fault, the print result, or neither: no such job.
public sealed record ReprintOutcome(PrintResult? Result, JournalFault? Fault)
{
    public bool NotFound => Result is null && Fault is null;
}

// Prints a stored job again: the one code path for HTTP and MCP. One call, one print: the stored blocks go through
// the same print path as a new job, and the journal gets a new row that names the first job.
// Public on purpose: the controller and the MCP tool take it as an injected parameter.
public sealed class PrintJobReprinter(IPrinterService printer, PrintJobLog jobLog, PrintJournalReader journal)
{
    // "logger" is the logger of the entry point: a fault is logged once, under its name.
    public async Task<ReprintOutcome> ReprintAsync(Guid jobId, string transport, string? source, ILogger logger, CancellationToken cancellationToken)
    {
        // No job yet: no row and no "Print job:" line.
        if (!journal.TryBeginReprint())
            return new ReprintOutcome(PrintResult.ReprintBusy, null);

        try
        {
            var (stored, fault) = await journal.TryReadAsync(
                token => journal.LoadForReprintAsync(jobId, token), logger, PrintJournalReader.JobRead, jobId, cancellationToken);
            if (fault is not null)
                return new ReprintOutcome(null, fault);
            if (stored is null)
                return new ReprintOutcome(null, null);

            PrintJobTrace.Current?.ReprintOf = stored.OriginalId;
            // The reason is one of the fixed texts of the reader: no row content.
            if (stored.Content is null)
                logger.LogWarning("Rejected reprint: job {JobId} has no full copy in the journal ({Reason})", jobId, stored.NoCopyReason);

            // The journal holds no full copy: a refused job, with the reason.
            var result = stored.Content is null
                ? PrintResult.Invalid(stored.NoCopyReason!)
                : await printer.PrintAsync(stored.Content, stored.Options);
            jobLog.Write(transport, source, result, stored.Content, stored.Options);
            return new ReprintOutcome(result, null);
        }
        finally
        {
            journal.EndReprint();
        }
    }
}
