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

// One search hit. Snippet is a short piece of the printed text around the first match, on one line.
public sealed record PrintJobSearchHit(PrintJobSummary Job, string Snippet);

// Newest first. Next is the "before" value that continues the search in the older jobs; null when no older job is left.
// A page can hold no hit and still have a Next: one call reads a limited number of jobs.
public sealed record PrintJobSearchResult(IReadOnlyList<PrintJobSearchHit> Hits, Guid? Next);

// Counts and sums only. Reprints is the number of printed jobs that are a reprint.
// Paper is the paper of the jobs that printed, in printer dots (8 dots per mm).
public sealed record PrintJobCounts(int Jobs, int Printed, int Reprints, long PaperDots);

// One UTC day.
public sealed record PrintJobDayStats(DateOnly Day, int Jobs, int Printed, long PaperDots);

// Source is the caller text of the job; null stands for the jobs with no source.
public sealed record PrintJobSourceStats(string? Source, int Jobs, int Printed, long PaperDots);

public sealed record PrintJobResultStats(string Result, int Jobs);

// The journal from From to To (UTC days, both in). ByDay holds every day of that time, oldest first.
// BySource holds the sources with the most jobs; MoreSources says that the list is cut.
public sealed record PrintJobStats(
    DateOnly From,
    DateOnly To,
    PrintJobCounts Totals,
    IReadOnlyList<PrintJobDayStats> ByDay,
    IReadOnlyList<PrintJobSourceStats> BySource,
    bool MoreSources,
    IReadOnlyList<PrintJobResultStats> ByResult);

// One papercut: the strips with the same subject line. Subject is printed text.
public sealed record PapercutEntry(string Subject, int Count, DateTime FirstAt, DateTime LastAt, Guid LastJobId);

// The papercut strips, grouped. Strips is the number of strips read; More says that the answer is cut: older jobs or strips were not read, or more subjects exist.
public sealed record PapercutLedger(IReadOnlyList<PapercutEntry> Papercuts, int Strips, bool More);
