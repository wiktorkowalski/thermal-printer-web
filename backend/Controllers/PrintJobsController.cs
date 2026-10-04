using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Controllers;

// The print journal over HTTP: list, one job, reprint. No auth (issue #51), so the answers hold allow-listed fields only
// (PrintJobDtos.cs). No endpoint here deletes a row.
[ApiController]
[Route("api/printer/jobs")]
[NoIndex]
public sealed class PrintJobsController(
    IPrinterService printerService,
    PrintJobLog jobLog,
    PrintJournalReader journal,
    ILogger<PrintJobsController> logger) : ControllerBase
{
    // A reprint call has no body: Kestrel refuses a large one, and the journal stores none (JournaledAttribute.NoBody).
    internal const int MaxReprintBodyBytes = 1024;

    // Fixed texts: no detail of the storage goes to the caller.
    internal const string JournalOffError = "The print journal is off";
    internal const string JournalUnavailableError = "The print journal is not available";
    internal const string InvalidIdError = "The job id is not valid";
    internal const string InvalidCursorError = "before is not a valid job id";
    internal const string NotFoundError = "Job not found";

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
        Guid? cursor = null;
        if (!string.IsNullOrEmpty(before))
        {
            if (!TryParseId(before, out var parsed))
                return BadRequest(new PrintResponse(false, InvalidCursorError, PrintResponse.ValidationType));
            cursor = parsed;
        }

        var (list, fault) = await ReadAsync(null, token => journal.ListAsync(cursor, limit, printed, token));
        return fault ?? Ok(list);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PrintJobDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PrintResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(string id)
    {
        if (!TryParseId(id, out var jobId))
            return BadRequest(new PrintResponse(false, InvalidIdError, PrintResponse.ValidationType));

        var (job, fault) = await ReadAsync(jobId, token => journal.GetAsync(jobId, token));
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
        if (!TryParseId(id, out var jobId))
            return BadRequest(new PrintResponse(false, InvalidIdError, PrintResponse.ValidationType));

        // No job yet: no row and no "Print job:" line.
        if (!journal.TryBeginReprint())
            return this.ToResponse(PrintResult.ReprintBusy);

        try
        {
            var (stored, fault) = await ReadAsync(jobId, token => journal.LoadForReprintAsync(jobId, token));
            if (fault is not null)
                return fault;
            if (stored is null)
                return JobNotFound();

            PrintJobTrace.Current?.ReprintOf = stored.OriginalId;

            // The journal holds no full copy: a refused job, with the reason.
            var result = stored.Content is null
                ? PrintResult.Invalid(stored.NoCopyReason!)
                : await printerService.PrintAsync(stored.Content, stored.Options);
            jobLog.Write(PrintJobLog.ReprintTransport, source, result, stored.Content, stored.Options);
            return this.ToResponse(result);
        }
        finally
        {
            journal.EndReprint();
        }
    }

    // One journal read. A fault answer in place of the value: the journal is off, not open yet, or the read failed.
    // No detail of the storage goes to the caller; the log gets one Warning with the job id.
    private async Task<(T? Value, IActionResult? Fault)> ReadAsync<T>(Guid? jobId, Func<CancellationToken, Task<T>> read)
    {
        if (!journal.IsOn)
            return (default, JournalFault(JournalOffError));

        try
        {
            return (await read(HttpContext.RequestAborted), null);
        }
        catch (Exception ex) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            if (jobId is null)
                logger.LogWarning(ex, "Journal read failed: the job list");
            else
                logger.LogWarning(ex, "Journal read failed: job {JobId}", jobId);
            return (default, JournalFault(JournalUnavailableError));
        }
    }

    // The form the list gives out. Any other text is not an id.
    private static bool TryParseId(string text, out Guid id) => Guid.TryParseExact(text, "D", out id);

    private NotFoundObjectResult JobNotFound() => NotFound(new PrintResponse(false, NotFoundError, PrintResponse.ValidationType));

    private ObjectResult JournalFault(string error)
        => StatusCode(StatusCodes.Status503ServiceUnavailable, new PrintResponse(false, error, PrintResponse.JournalType));
}

// The journal holds what was printed: no search engine indexes it and no cache keeps it.
// Row text is caller text, so no browser may guess a content type other than JSON for it.
[AttributeUsage(AttributeTargets.Class)]
internal sealed class NoIndexAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var headers = context.HttpContext.Response.Headers;
        headers["X-Robots-Tag"] = "noindex";
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
    }
}
