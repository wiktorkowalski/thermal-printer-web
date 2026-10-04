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
public class PrintJobsController(
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
    internal const string ReprintBusyError = "Server busy: another reprint runs. Send it again in a few seconds.";

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

        if (!journal.IsOn)
            return JournalFault(JournalOffError);

        try
        {
            return Ok(await journal.ListAsync(cursor, limit, printed, HttpContext.RequestAborted));
        }
        catch (Exception ex) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Journal read failed: the job list");
            return JournalFault(JournalUnavailableError);
        }
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

        if (!journal.IsOn)
            return JournalFault(JournalOffError);

        try
        {
            var job = await journal.GetAsync(jobId, HttpContext.RequestAborted);
            return job is null ? JobNotFound() : Ok(job);
        }
        catch (Exception ex) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Journal read failed: job {JobId}", jobId);
            return JournalFault(JournalUnavailableError);
        }
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

        if (!journal.IsOn)
            return JournalFault(JournalOffError);

        if (!journal.TryBeginReprint())
        {
            Response.Headers.RetryAfter = PrintResultResponse.BusyRetryAfterSeconds;
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new PrintResponse(false, ReprintBusyError, PrintResponse.BusyType));
        }

        try
        {
            StoredJob? stored;
            try
            {
                stored = await journal.LoadForReprintAsync(jobId, HttpContext.RequestAborted);
            }
            catch (Exception ex) when (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Journal read failed: job {JobId}", jobId);
                return JournalFault(JournalUnavailableError);
            }

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
