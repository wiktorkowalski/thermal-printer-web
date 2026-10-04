using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Journal;

// How a reprint call ended. Exactly one of the three is set: a journal fault, "no such job", or the print result.
public sealed record ReprintOutcome(PrintResult? Result, JournalFault? Fault, bool NotFound);

// Prints a stored job again: the one code path for HTTP and MCP. One call, one print: the stored blocks go through
// the same print path as a new job, and the journal gets a new row that names the first job.
// Public on purpose: the controller and the MCP tool take it as an injected parameter. Program.cs builds it.
public sealed class PrintJobReprinter
{
    private readonly IPrinterService _printer;
    private readonly PrintJobLog _jobLog;
    private readonly PrintJournalReader _journal;

    internal PrintJobReprinter(IPrinterService printer, PrintJobLog jobLog, PrintJournalReader journal)
    {
        _printer = printer;
        _jobLog = jobLog;
        _journal = journal;
    }

    // "logger" is the logger of the entry point: a fault is logged once, under its name.
    public async Task<ReprintOutcome> ReprintAsync(Guid jobId, string transport, string? source, ILogger logger, CancellationToken cancellationToken)
    {
        // No job yet: no row and no "Print job:" line.
        if (!_journal.TryBeginReprint())
            return new ReprintOutcome(PrintResult.ReprintBusy, null, false);

        try
        {
            var (stored, fault) = await _journal.TryReadAsync(
                token => _journal.LoadForReprintAsync(jobId, token), logger, PrintJournalReader.JobRead, jobId, cancellationToken);
            if (fault is not null)
                return new ReprintOutcome(null, fault, false);
            if (stored is null)
                return new ReprintOutcome(null, null, true);

            PrintJobTrace.Current?.ReprintOf = stored.OriginalId;
            // The reason is one of the fixed texts of the reader: no row content.
            if (stored.Content is null)
                logger.LogWarning("Rejected reprint: job {JobId} has no full copy in the journal ({Reason})", jobId, stored.NoCopyReason);

            // The journal holds no full copy: a refused job, with the reason.
            var result = stored.Content is null
                ? PrintResult.Invalid(stored.NoCopyReason!)
                : await _printer.PrintAsync(stored.Content, stored.Options);
            _jobLog.Write(transport, source, result, stored.Content, stored.Options);
            return new ReprintOutcome(result, null, false);
        }
        finally
        {
            _journal.EndReprint();
        }
    }
}
