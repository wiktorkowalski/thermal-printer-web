using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Controllers;

// The print journal over HTTP: list, one job, reprint, statistics, search, papercut ledger.
// No auth (issue #51), so the answers hold allow-listed fields only (PrintJobDtos.cs). No endpoint here deletes a row.
[ApiController]
[Route("api/printer/jobs")]
[JournalAnswerHeaders]
public sealed class PrintJobsController(
    PrintJournalReader journal,
    PrintJobReprinter reprinter,
    ILogger<PrintJobsController> logger) : ControllerBase
{
    // A reprint call has no body. Nothing reads one, and the journal stores none (JournaledAttribute.NoBody);
    // the limit is for any later code that does read it.
    internal const int MaxReprintBodyBytes = 1024;

    // Fixed texts: no detail of the storage goes to the caller.
    internal const string InvalidIdError = "The job id is not valid";
    internal const string InvalidCursorError = "before is not a valid job id";
    internal const string NotFoundError = "Job not found";
    internal static readonly string InvalidQueryError =
        $"q must hold {PrintJournalReader.MinQueryLength} to {PrintJournalReader.MaxQueryLength} characters";

    // Other journal queries run. They take a few seconds at most.
    private const string BusyRetryAfterSeconds = "5";

    // Newest first. "before" is the "next" value of the page before; "printed=true" leaves out every job that did not print.
    [HttpGet]
    [ProducesResponseType(typeof(PrintJobList), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> List(
        [FromQuery] string? before = null,
        [FromQuery] int limit = PrintJournalReader.DefaultPageSize,
        [FromQuery] bool printed = false)
    {
        if (!TryParseCursor(before, out var cursor))
            return Invalid(InvalidCursorError);

        var (list, fault) = await ReadAsync(PrintJournalReader.ListRead, null, token => journal.ListAsync(cursor, limit, printed, token));
        return fault ?? Ok(list);
    }

    // Counts and sums for the last "days" UTC days: no row content but the source names.
    [HttpGet("stats")]
    [ProducesResponseType(typeof(PrintJobStats), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Stats([FromQuery] int days = PrintJournalReader.DefaultStatsDays)
    {
        var (stats, fault) = await ReadAsync(PrintJournalReader.StatsRead, null, token => journal.StatsAsync(days, token));
        return fault ?? Ok(stats);
    }

    // Finds "q" in the printed text, newest first. "before" is the "next" value of the answer before.
    // The query text goes to no log.
    [HttpGet("search")]
    [ProducesResponseType(typeof(PrintJobSearchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Search(
        [FromQuery] string? q = null,
        [FromQuery] string? before = null,
        [FromQuery] int limit = PrintJournalReader.DefaultPageSize)
    {
        if (PrintJournalReader.CleanQuery(q) is not { } query)
            return Invalid(InvalidQueryError);
        if (!TryParseCursor(before, out var cursor))
            return Invalid(InvalidCursorError);

        var (result, fault) = await ReadAsync(PrintJournalReader.SearchRead, null, token => journal.SearchAsync(query, cursor, limit, token));
        return fault ?? Ok(result);
    }

    // The strips with the papercut header, grouped by subject.
    [HttpGet("papercuts")]
    [ProducesResponseType(typeof(PapercutLedger), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Papercuts()
    {
        var (ledger, fault) = await ReadAsync(PrintJournalReader.PapercutsRead, null, journal.PapercutsAsync);
        return fault ?? Ok(ledger);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PrintJobDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(string id)
    {
        if (!PrintJournalReader.TryParseId(id, out var jobId))
            return Invalid(InvalidIdError);

        var (job, fault) = await ReadAsync(PrintJournalReader.JobRead, jobId, token => journal.GetAsync(jobId, token));
        return fault ?? (job is null ? JobNotFound() : Ok(job));
    }

    // One call, one print: the stored blocks go through the same print path as a new job, and the journal gets a new row.
    [HttpPost("{id}/reprint")]
    [Journaled(JobsOnly = true, NoBody = true)]
    [RequestSizeLimit(MaxReprintBodyBytes)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Reprint(string id, [FromQuery] string? source = null)
    {
        if (!PrintJournalReader.TryParseId(id, out var jobId))
            return Invalid(InvalidIdError);

        var outcome = await reprinter.ReprintAsync(jobId, PrintJobLog.ReprintTransport, source, logger, HttpContext.RequestAborted);
        return outcome switch
        {
            { Fault: { } fault } => JournalFaultAnswer(fault),
            { NotFound: true } => JobNotFound(),
            _ => this.ToResponse(outcome.Result!)
        };
    }

    // One journal read. A fault answer in place of the value: the journal is off, not open yet, busy, or the read failed.
    private async Task<(T? Value, IActionResult? Fault)> ReadAsync<T>(string what, Guid? jobId, Func<CancellationToken, Task<T>> read)
    {
        var (value, fault) = await journal.TryReadAsync(read, logger, what, jobId, HttpContext.RequestAborted);
        return (value, fault is null ? null : JournalFaultAnswer(fault));
    }

    private ObjectResult JournalFaultAnswer(JournalFault fault)
    {
        if (fault == JournalFault.Busy)
            Response.Headers.RetryAfter = BusyRetryAfterSeconds;
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new PrintResponse(false, fault.Error, fault.Type));
    }

    // No value is no cursor: the first page.
    private static bool TryParseCursor(string? before, out Guid? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(before))
            return true;
        if (!PrintJournalReader.TryParseId(before, out var id))
            return false;

        cursor = id;
        return true;
    }

    private BadRequestObjectResult Invalid(string error) => BadRequest(new PrintResponse(false, error, PrintResponse.ValidationType));

    private NotFoundObjectResult JobNotFound() => NotFound(new PrintResponse(false, NotFoundError, PrintResponse.ValidationType));
}

// The journal holds what was printed: no search engine indexes it and no cache keeps it.
// Row text is caller text, so no browser may guess a content type other than JSON for it.
// "Always run": also on an answer that a filter gives before the action, such as the 400 for a query value that does not bind.
[AttributeUsage(AttributeTargets.Class)]
internal sealed class JournalAnswerHeadersAttribute : Attribute, IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        var headers = context.HttpContext.Response.Headers;
        headers["X-Robots-Tag"] = "noindex";
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
