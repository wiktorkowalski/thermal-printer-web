using System.Diagnostics;

namespace ThermalPrinterWeb.Services.Journal;

// Marks an endpoint whose POST requests go to the journal.
// JobsOnly: only a request that got to a print (PrintJobLog). The MCP endpoint also serves
// calls that are not jobs (initialize, tools/list, get_status, beep).
[AttributeUsage(AttributeTargets.Method)]
internal sealed class JournaledAttribute : Attribute
{
    public bool JobsOnly { get; init; }

    // The endpoint takes no body (reprint): what a caller sends there is not stored.
    public bool NoBody { get; init; }
}

// Collects one journal entry per request, around the endpoint: the request as it came,
// the caller, the facts of the job (PrintJobTrace) and how the request ended.
internal sealed class PrintJournalMiddleware(RequestDelegate next, PrintJournal journal, ILogger<PrintJournalMiddleware> logger)
{
    internal const string Redacted = "[redacted]";

    // A header with one of these in its name holds a credential: the journal keeps the name and drops the value.
    private static readonly string[] CredentialNameParts = ["auth", "cookie", "token", "secret", "passw", "key", "jwt", "session", "signature", "credential", "bearer", "dpop", "otp"];

    // The endpoint read the body, so this is a copy in memory. The time limit is for a body the endpoint did not read.
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(2);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!journal.IsOn
            || !HttpMethods.IsPost(context.Request.Method)
            || context.GetEndpoint()?.Metadata.GetMetadata<JournaledAttribute>() is not { } journaled)
        {
            await next(context);
            return;
        }

        var createdAt = DateTime.UtcNow;
        var started = Stopwatch.GetTimestamp();
        var trace = PrintJobTrace.Begin();
        var buffered = !journaled.NoBody && TryBufferBody(context.Request, trace);

        Exception? fault = null;
        try
        {
            // The job id in the log scope ties the log lines of a job to its journal row.
            using (logger.BeginScope(new Dictionary<string, object> { ["JobId"] = trace.Id }))
                await next(context);
        }
        catch (Exception ex)
        {
            fault = ex;
            // An exception that left the endpoint. It wins over one the print path stored and handled.
            trace.Exception = ex;
            throw;
        }
        finally
        {
            PrintJobTrace.End();
            if (trace.Outcome is not null || !journaled.JobsOnly)
                await RecordAsync(context, trace, createdAt, Stopwatch.GetElapsedTime(started), buffered, fault);
        }
    }

    // In memory up to the request body limit: no temp file, so a full disk cannot fail the request here.
    private bool TryBufferBody(HttpRequest request, PrintJobTrace trace)
    {
        try
        {
            request.EnableBuffering((int)PrinterService.MaxRequestBodyBytes, PrinterService.MaxRequestBodyBytes);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Journal: the request body of job {JobId} is not stored", trace.Id);
            return false;
        }
    }

    // Never throws: the caller has its print result, or gets it from the code after this.
    private async Task RecordAsync(
        HttpContext context, PrintJobTrace trace, DateTime createdAt, TimeSpan duration, bool buffered, Exception? fault)
    {
        try
        {
            // The caller gets the whole response before the body is copied.
            if (fault is null)
                await context.Response.CompleteAsync();
        }
        catch (Exception ex)
        {
            // The caller went away. The job ran, so its row is still stored.
            logger.LogDebug(ex, "Journal: the response of job {JobId} did not complete", trace.Id);
        }

        try
        {
            // The row is built here, after the response: it holds a hash for each picture, so the
            // queue of the writer keeps no block content alive.
            var entry = new PrintJobEntry
            {
                Trace = trace,
                CreatedAt = createdAt,
                Duration = duration,
                HttpStatus = fault switch
                {
                    null => context.Response.StatusCode,
                    // The request body limit: Kestrel answers 413 after the exception leaves the pipeline.
                    BadHttpRequestException bad => bad.StatusCode,
                    _ => StatusCodes.Status500InternalServerError
                },
                UserAgent = context.Request.Headers.UserAgent.ToString(),
                RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
                Headers = Headers(context.Request),
                Request = buffered ? await ReadBodyAsync(context.Request) : null
            };
            journal.Add(entry.ToRow());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Journal: job {JobId} is not stored", trace.Id);
        }
    }

    private static Dictionary<string, string[]> Headers(HttpRequest request)
        => request.Headers.ToDictionary(
            header => header.Key,
            header => IsCredential(header.Key) ? [Redacted] : header.Value.Select(value => value ?? string.Empty).ToArray());

    internal static bool IsCredential(string headerName)
        => CredentialNameParts.Any(part => headerName.Contains(part, StringComparison.OrdinalIgnoreCase));

    // Null: the body did not arrive in full (over the size limit, or the caller stopped).
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request)
    {
        try
        {
            using var timeout = new CancellationTokenSource(BodyReadTimeout);
            request.Body.Position = 0;
            using var copy = new MemoryStream((int)Math.Min(request.ContentLength ?? 0, PrinterService.MaxRequestBodyBytes));
            await request.Body.CopyToAsync(copy, timeout.Token);
            // No second copy when the capacity was right.
            return copy.Capacity == copy.Length ? copy.GetBuffer() : copy.ToArray();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or BadHttpRequestException or ObjectDisposedException)
        {
            return null;
        }
    }
}
