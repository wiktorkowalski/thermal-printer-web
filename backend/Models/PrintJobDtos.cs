using System.Text.Json;

namespace ThermalPrinterWeb.Models;

// What the journal read endpoints serve. The endpoints are public: each record is an allow-list.
// A new property here needs a decision, and JournalReadTests pins the names.
// Never served: the caller address, the User-Agent, the request headers, the exception, the log lines, the request body, the printer data.

// One job in a list: the small facts only.
public sealed record PrintJobSummary(
    Guid Id,
    DateTime CreatedAt,
    string Transport,
    string? Source,
    string Result,
    string? Error,
    string? Title,
    int? BlockCount,
    int? PaperDots,
    Guid? ReprintOf,
    bool CanReprint);

// Newest first. Next is the "before" value of the next page; null on the last page.
public sealed record PrintJobList(IReadOnlyList<PrintJobSummary> Jobs, Guid? Next);

// One job with its content. Blocks and Options are the API JSON of the job; an image block holds a hash, not the picture.
public sealed record PrintJobDetail(PrintJobSummary Job, JsonElement? Blocks, JsonElement? Options);
