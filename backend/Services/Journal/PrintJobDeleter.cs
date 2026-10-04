using System.Security.Cryptography;

namespace ThermalPrinterWeb.Services.Journal;

// Which jobs a delete takes: one id, or every job that fits all the given values. From is in the range, To is not; both UTC.
// Only an id, a time range and the exact source: no value here is matched against printed text.
internal sealed record JobDeleteFilter(Guid? Id = null, string? Source = null, DateTime? From = null, DateTime? To = null);

// The jobs that fit a filter at the time of a dry run. Ids is empty when more than the limit fit.
// Reprints: the reprint rows of those jobs that do not fit the filter themselves.
internal sealed record JobSelection(IReadOnlyList<Guid> Ids, int Jobs, int Reprints);

// The answer of a dry run. Code is null when no job fits or more than the limit fit: then nothing can be deleted.
internal sealed record DeletePreview(int Jobs, int Reprints, Guid? FirstId, Guid? LastId, string? Code);

internal enum DeleteState
{
    Deleted,
    // The code is not known, was used, is too old, or belongs to other arguments.
    NoCode,
    // A job of the dry run is gone.
    Changed
}

internal sealed record DeleteOutcome(DeleteState State, int Jobs = 0, int Reprints = 0, Guid? FirstId = null, Guid? LastId = null);

// Deletes journal rows, in two steps. Only the MCP tools call it: no HTTP endpoint deletes a row.
// Step 1, the dry run: reads the ids of the jobs that fit and keeps them in memory under a random code.
// Step 2, the delete: takes the code once and deletes exactly those jobs, with their reprint rows.
// So a call that did not see the answer of a dry run deletes nothing, and a job that came after the dry run is never in the set.
// The print API is open, so anyone can add journal rows at any time. A delete does not depend on that traffic:
// new rows do not change the set of a code, the row limit counts the jobs of the filter only, and the writer runs a delete
// before the writes that wait (PrintJournal).
// A reprint row holds no copy of its job, so it goes with its first job: alone it would say that it can be printed again, and then fail.
// Each delete writes one Information line: who (transport, User-Agent), the filter, the row counts and the id range. Never row content.
// A delete is not a print: it stores no journal row.
// Public on purpose: the MCP tools take it as an injected parameter. The constructor is internal, so Program.cs builds it.
public sealed class PrintJobDeleter
{
    // The most jobs one call deletes. A dry run that fits more gives no code: the caller sends a shorter time range.
    internal const int MaxRows = 1000;

    // Dry runs whose code can still be used. The oldest goes first.
    internal const int MaxPendingCodes = 8;

    // Fixed texts for the log of a failed call.
    internal const string PreviewRead = "the jobs of a delete";
    internal const string DeleteRun = "a delete";

    private const int CodeBytes = 8;

    private readonly PrintJournal _journal;
    private readonly PrintJournalReader _reader;
    private readonly PrintJobLog _jobLog;
    private readonly ILogger<PrintJobDeleter> _logger;

    private readonly Lock _pendingLock = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private sealed record Pending(JobDeleteFilter Filter, IReadOnlyList<Guid> Ids, long CreatedAt);

    internal PrintJobDeleter(PrintJournal journal, PrintJournalReader reader, PrintJobLog jobLog, ILogger<PrintJobDeleter> logger)
    {
        _journal = journal;
        _reader = reader;
        _jobLog = jobLog;
        _logger = logger;
    }

    // How long the code of a dry run can be used. A test sets it.
    internal TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    // The dry run. Deletes nothing. A fault in place of the value: the journal is off, not open yet, or the read failed.
    internal Task<(DeletePreview? Value, JournalFault? Fault)> PreviewAsync(JobDeleteFilter filter, CancellationToken cancellationToken)
        => _reader.TryReadAsync(token => ReadPreviewAsync(filter, token), _logger, PreviewRead, filter.Id, cancellationToken);

    // Deletes the jobs of the dry run that gave this code. The code works once, also when the call is refused.
    // A fault in place of the value: the journal is off or the delete failed; then no row is deleted.
    internal Task<(DeleteOutcome? Value, JournalFault? Fault)> DeleteAsync(
        string transport, JobDeleteFilter filter, string code, CancellationToken cancellationToken)
        => _reader.TryReadAsync(token => RunDeleteAsync(transport, filter, code, token), _logger, DeleteRun, filter.Id, cancellationToken);

    private async Task<DeletePreview> ReadPreviewAsync(JobDeleteFilter filter, CancellationToken cancellationToken)
    {
        var selection = await _reader.SelectForDeleteAsync(filter, MaxRows, cancellationToken);
        if (selection.Ids.Count == 0)
            return new DeletePreview(selection.Jobs, selection.Reprints, null, null, null);

        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(CodeBytes));
        lock (_pendingLock)
        {
            if (_pending.Count >= MaxPendingCodes)
                _pending.Remove(_pending.MinBy(entry => entry.Value.CreatedAt).Key);
            _pending[code] = new Pending(filter, selection.Ids, Environment.TickCount64);
        }

        // The id is a GUID v7 and the list is in its order: the order in time.
        return new DeletePreview(selection.Jobs, selection.Reprints, selection.Ids[0], selection.Ids[^1], code);
    }

    private async Task<DeleteOutcome> RunDeleteAsync(string transport, JobDeleteFilter filter, string code, CancellationToken cancellationToken)
    {
        Pending? pending;
        lock (_pendingLock)
            _pending.Remove(code, out pending);

        if (pending is null
            || pending.Filter != filter
            || TimeSpan.FromMilliseconds(Environment.TickCount64 - pending.CreatedAt) > CodeLifetime)
        {
            return new DeleteOutcome(DeleteState.NoCode);
        }

        var ids = pending.Ids;
        // Read here: the writer runs outside the request.
        var userAgent = _jobLog.CallerUserAgent();
        var rows = await _journal.DeleteAsync(ids, WriteAuditLine, cancellationToken);
        return rows is { } deleted
            ? new DeleteOutcome(DeleteState.Deleted, ids.Count, deleted - ids.Count, ids[0], ids[^1])
            : new DeleteOutcome(DeleteState.Changed);

        // The source is the filter of the caller, not row content.
        void WriteAuditLine(int deletedRows) => _logger.LogInformation(
            "Journal delete: transport={Transport} userAgent=\"{UserAgent}\" id={Id} source=\"{Source}\" from={From} to={To} "
            + "jobs={Jobs} reprints={Reprints} firstId={FirstId} lastId={LastId}",
            transport,
            userAgent,
            filter.Id?.ToString() ?? LogSafeText.Missing,
            LogSafeText.Clean(filter.Source, PrintJobLog.MaxSourceLength),
            filter.From?.ToString("O") ?? LogSafeText.Missing,
            filter.To?.ToString("O") ?? LogSafeText.Missing,
            ids.Count,
            deletedRows - ids.Count,
            ids[0],
            ids[^1]);
    }
}
