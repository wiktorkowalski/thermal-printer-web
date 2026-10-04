using Microsoft.EntityFrameworkCore;

namespace ThermalPrinterWeb.Services.Journal;

// Which jobs a delete takes: one id, or every job that fits all the given values. From is in the range, To is not; both UTC.
internal sealed record JobDeleteFilter(Guid? Id = null, string? Source = null, DateTime? From = null, DateTime? To = null);

// The rows that a delete takes, or took. Jobs: the rows that fit the filter. Reprints: the reprint rows of those jobs
// that do not fit the filter themselves. A reprint row holds no copy of its job, so it goes with its first job:
// alone it would say that it can be printed again, and then fail.
internal sealed record JobSelection(int Jobs, int Reprints, Guid? FirstId, Guid? LastId, bool Deleted = false)
{
    public int Rows => Jobs + Reprints;

    // The filter values are parameters: caller text never becomes SQL.
    public static IQueryable<PrintJob> Matched(JournalDbContext db, JobDeleteFilter filter)
    {
        var jobs = db.PrintJobs.AsQueryable();
        if (filter.Id is { } id)
            jobs = jobs.Where(job => job.Id == id);
        if (filter.Source is { } source)
            jobs = jobs.Where(job => job.Source == source);
        if (filter.From is { } from)
            jobs = jobs.Where(job => job.CreatedAt >= from);
        if (filter.To is { } to)
            jobs = jobs.Where(job => job.CreatedAt < to);
        return jobs;
    }

    // The matched jobs and their reprint rows. A reprint of a reprint names the first job, so one step is enough.
    // ReprintOf has no index: this reads the small PrintJobs rows only, never a payload.
    public static IQueryable<PrintJob> WithReprints(JournalDbContext db, JobDeleteFilter filter)
    {
        var matched = Matched(db, filter);
        return db.PrintJobs.Where(job => matched.Any(first => first.Id == job.Id || first.Id == job.ReprintOf));
    }

    public static async Task<JobSelection> ReadAsync(JournalDbContext db, JobDeleteFilter filter, CancellationToken cancellationToken)
    {
        var rows = WithReprints(db, filter);
        var jobs = await Matched(db, filter).CountAsync(cancellationToken);
        var all = await rows.CountAsync(cancellationToken);
        // The id is a GUID v7: its order is the order in time.
        var first = await rows.OrderBy(job => job.Id).Select(job => (Guid?)job.Id).FirstOrDefaultAsync(cancellationToken);
        var last = await rows.OrderByDescending(job => job.Id).Select(job => (Guid?)job.Id).FirstOrDefaultAsync(cancellationToken);
        return new JobSelection(jobs, all - jobs, first, last);
    }
}

// Deletes journal rows. Only the MCP tools call it: no HTTP endpoint deletes a row.
// The delete runs in the one journal writer, between two writes. Each delete that took rows writes one Information line:
// who (transport, User-Agent), the filter, the row counts and the id range. Never row content.
// A delete is not a print: it stores no journal row.
// Public on purpose: the MCP tools take it as an injected parameter. The constructor is internal, so Program.cs builds it.
public sealed class PrintJobDeleter
{
    // The most rows one call deletes. A call that fits more is refused: the caller sends a shorter time range.
    internal const int MaxRows = 1000;

    // Fixed texts for the log of a failed call.
    internal const string PreviewRead = "the rows of a delete";
    internal const string DeleteRun = "a delete";

    private readonly PrintJournal _journal;
    private readonly IHttpContextAccessor _httpContext;
    private readonly ILogger<PrintJobDeleter> _logger;

    internal PrintJobDeleter(PrintJournal journal, IHttpContextAccessor httpContext, ILogger<PrintJobDeleter> logger)
    {
        _journal = journal;
        _httpContext = httpContext;
        _logger = logger;
    }

    // Deletes the rows of the filter when their number is at most MaxRows and, with confirmRows, equal to it.
    // The answer says whether the rows are deleted, and how many fit.
    internal Task<JobSelection> DeleteAsync(string transport, JobDeleteFilter filter, int? confirmRows, CancellationToken cancellationToken)
    {
        // Read here: the writer runs outside the request.
        var userAgent = LogSafeText.Clean(_httpContext.HttpContext?.Request.Headers.UserAgent.ToString(), PrintJobLog.MaxUserAgentLength);

        return _journal.DeleteAsync(filter, confirmRows, MaxRows, WriteAuditLine, cancellationToken);

        // The source is the filter of the caller, not row content.
        void WriteAuditLine(JobSelection deleted) => _logger.LogInformation(
            "Journal delete: transport={Transport} userAgent=\"{UserAgent}\" id={Id} source=\"{Source}\" from={From} to={To} "
            + "jobs={Jobs} reprints={Reprints} firstId={FirstId} lastId={LastId}",
            transport,
            userAgent,
            filter.Id?.ToString() ?? LogSafeText.Missing,
            LogSafeText.Clean(filter.Source, PrintJobLog.MaxSourceLength),
            filter.From?.ToString("O") ?? LogSafeText.Missing,
            filter.To?.ToString("O") ?? LogSafeText.Missing,
            deleted.Jobs,
            deleted.Reprints,
            deleted.FirstId,
            deleted.LastId);
    }
}
