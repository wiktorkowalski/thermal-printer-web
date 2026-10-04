namespace ThermalPrinterWeb.Services.Journal;

// Stored as the number: a rename keeps the stored rows valid. Keep every value; add new ones at the end.
internal enum JobResult
{
    Printed = 0,
    Validation = 1,
    Printer = 2,
    Busy = 3,
    // The request ended with a fault outside the print path (an unhandled exception).
    Fault = 4
}

// One row per journaled request: the small facts, for lists and statistics.
internal sealed class PrintJob
{
    public Guid Id { get; set; }

    // UTC.
    public DateTime CreatedAt { get; set; }

    // The whole request, in milliseconds.
    public long DurationMs { get; set; }

    // "http", "mcp:print" or "mcp:print_note".
    public required string Transport { get; set; }
    public string? Source { get; set; }
    public string? UserAgent { get; set; }

    // The address of the connection: the proxy, when there is one. The forwarding headers are in the payload.
    public string? RemoteIp { get; set; }

    public JobResult Result { get; set; }
    public string? Error { get; set; }
    public int HttpStatus { get; set; }

    public string? Title { get; set; }
    public int? BlockCount { get; set; }
    public int? ByteCount { get; set; }
    public int? PaperDots { get; set; }

    // The PrinterStatus read before the job, as JSON. Null: the job did not get that far.
    public string? PrinterStatus { get; set; }

    public long RequestBytes { get; set; }
    public required string AppVersion { get; set; }

    public PrintJobPayload Payload { get; set; } = null!;
}

// The large values of a job, in their own table: a list query over PrintJob reads none of them.
internal sealed class PrintJobPayload
{
    public Guid JobId { get; set; }

    // The request body as it came: the HTTP JSON, or the JSON-RPC call with the tool name and its arguments.
    public byte[]? Request { get; set; }

    // The ESC/POS bytes of the job. Null: the document was not built.
    public byte[]? Bytes { get; set; }

    // The blocks and options the print path got, as API JSON. An image is a hash here; the picture is in Request.
    public string? Blocks { get; set; }
    public string? Options { get; set; }

    // The Text blocks, one per line.
    public string? PlainText { get; set; }

    // JSON object: header name to its values. A credential header holds "[redacted]".
    public required string Headers { get; set; }

    public string? Exception { get; set; }

    // The server log lines of the request.
    public string? Log { get; set; }

    // What the row holds in memory while it waits for the writer, and what it adds to the database.
    // The text values are small: Blocks holds a hash in place of each picture.
    public long LargeBytes() => (Request?.Length ?? 0L) + (Bytes?.Length ?? 0L);
}
