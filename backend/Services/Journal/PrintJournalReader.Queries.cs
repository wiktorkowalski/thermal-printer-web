using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Journal;

// MaxQueries statistics, search or ledger reads run already.
internal sealed class JournalBusyException : Exception;

// The reads that go over many rows: statistics, search, papercut ledger. The endpoints have no auth, so each read has
// a fixed upper cost, and only MaxQueries of them run at a time.
public sealed partial class PrintJournalReader
{
    internal const int DefaultStatsDays = 30;
    internal const int MaxStatsDays = 365;
    internal const int MaxStatsSources = 20;

    internal const int MinQueryLength = 2;
    internal const int MaxQueryLength = 100;
    internal const int MaxSnippetLength = 160;

    // One search call reads at most this many jobs, in batches, and stops at its time budget.
    // The answer then holds a "next" value: the caller goes on from there.
    internal const int SearchBatchRows = 100;
    internal const int MaxSearchedRows = 5000;
    internal static readonly TimeSpan DefaultSearchBudget = TimeSpan.FromSeconds(2);

    // The fixed header of a papercut strip (issue #41): "PAPERCUT" or "PAPERCUT xN" as the first line.
    internal const string PapercutHeader = "PAPERCUT";
    internal const int MaxPapercutJobs = 20_000;
    internal const int MaxPapercutStrips = 500;
    internal const int MaxPapercuts = 100;
    internal const int MaxSubjectLength = 100;

    // The journal page of the web app reads the statistics, then the ledger, then searches: one at a time.
    internal const int MaxQueries = 4;

    // Fixed texts for the log.
    internal const string StatsRead = "the statistics";
    internal const string SearchRead = "the search";
    internal const string PapercutsRead = "the papercut ledger";

    // The text before the subject can be on the header line or on the lines after it.
    private const int PapercutHeadLength = 400;

    // Characters of the text before the match in a snippet.
    private const int SnippetLead = 40;

    private readonly SemaphoreSlim _queryGate = new(MaxQueries, MaxQueries);

    // "PAPERCUT", "PAPERCUT x3", "PAPERCUT: text". Not "PAPERCUTS" and not "PAPERCUTTER".
    [GeneratedRegex(@"^PAPERCUT(?:\s*[x×]\s*\d+)?(?![\p{L}\p{N}])[\s:.!\-–—]*(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PapercutHeaderLine();

    // Counts and sums over PrintJobs for the last "days" UTC days, today included. No row text but the source names.
    public Task<PrintJobStats> StatsAsync(int days, CancellationToken cancellationToken) => QueryAsync(async () =>
    {
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(1 - Math.Clamp(days, 1, MaxStatsDays));
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        using var timeout = Timeout(cancellationToken);
        await using var db = Open();
        var window = db.PrintJobs.AsNoTracking().Where(job => job.CreatedAt >= start && job.CreatedAt < end);

        // One read for the days and the results: at most one row per day and result.
        var cells = await window
            .GroupBy(job => new { Day = job.CreatedAt.Date, job.Result })
            .Select(group => new
            {
                group.Key.Day,
                group.Key.Result,
                Jobs = group.Count(),
                Reprints = group.Count(job => job.ReprintOf != null),
                PaperDots = group.Sum(job => (long)(job.PaperDots ?? 0))
            })
            .ToListAsync(timeout.Token);
        var perDay = cells
            .GroupBy(cell => DateOnly.FromDateTime(cell.Day))
            .ToDictionary(day => day.Key, day =>
            {
                var printed = day.Where(cell => cell.Result == JobResult.Printed).ToList();
                return new
                {
                    Jobs = day.Sum(cell => cell.Jobs),
                    Printed = printed.Sum(cell => cell.Jobs),
                    // Like "Printed": the reprints that printed.
                    Reprints = printed.Sum(cell => cell.Reprints),
                    PaperDots = printed.Sum(cell => cell.PaperDots)
                };
            });

        // "source" is caller text: a caller can make any number of names. The answer holds the largest only.
        var sources = await window
            .GroupBy(job => job.Source)
            .Select(group => new
            {
                Source = group.Key,
                Jobs = group.Count(),
                Printed = group.Count(job => job.Result == JobResult.Printed),
                PaperDots = group.Sum(job => job.Result == JobResult.Printed ? (long)(job.PaperDots ?? 0) : 0L)
            })
            .OrderByDescending(source => source.Jobs)
            .ThenBy(source => source.Source)
            .Take(MaxStatsSources + 1)
            .ToListAsync(timeout.Token);

        var results = cells
            .GroupBy(cell => cell.Result)
            .Select(result => new { Result = result.Key, Jobs = result.Sum(cell => cell.Jobs) });

        var byDay = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(from.AddDays)
            .Select(day => perDay.TryGetValue(day, out var counts)
                ? new PrintJobDayStats(day, counts.Jobs, counts.Printed, counts.PaperDots)
                : new PrintJobDayStats(day, 0, 0, 0))
            .ToList();

        return new PrintJobStats(
            from,
            to,
            new PrintJobCounts(
                perDay.Values.Sum(day => day.Jobs),
                perDay.Values.Sum(day => day.Printed),
                perDay.Values.Sum(day => day.Reprints),
                perDay.Values.Sum(day => day.PaperDots)),
            byDay,
            [.. sources.Take(MaxStatsSources).Select(source => new PrintJobSourceStats(source.Source, source.Jobs, source.Printed, source.PaperDots))],
            sources.Count > MaxStatsSources,
            [.. results.OrderBy(result => result.Result).Select(result => new PrintJobResultStats(result.Result.ToString(), result.Jobs))]);
    });

    // Finds "query" in the printed text of the jobs, newest first; letter case does not matter.
    // The query never goes into SQL: the rows come in batches by id and the match runs here. So "%" and "_" are plain
    // characters, and the cost of one call has a limit that does not depend on the query or on the size of the journal.
    // A reprint row has no text of its own: the hit is its first job.
    public Task<PrintJobSearchResult> SearchAsync(string query, Guid? before, int limit, CancellationToken cancellationToken) => QueryAsync(async () =>
    {
        var pageSize = Math.Clamp(limit, 1, MaxPageSize);
        var started = Stopwatch.GetTimestamp();
        using var timeout = Timeout(cancellationToken);
        await using var db = Open();

        List<(Guid Id, string Snippet)> found = [];
        var cursor = before;
        Guid? next = null;
        var read = 0;
        while (true)
        {
            var texts = db.PrintJobTexts.AsNoTracking();
            if (cursor is { } last)
                texts = texts.Where(text => text.JobId.CompareTo(last) < 0);
            var batch = await texts
                .OrderByDescending(text => text.JobId)
                .Take(SearchBatchRows)
                .Select(text => new { text.JobId, text.Text })
                .ToListAsync(timeout.Token);

            foreach (var row in batch)
            {
                cursor = row.JobId;
                var at = row.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    continue;

                found.Add((row.JobId, Snippet(row.Text, at)));
                if (found.Count == pageSize)
                    break;
            }

            read += batch.Count;
            var pageIsFull = found.Count == pageSize;
            // The oldest job is read.
            if (!pageIsFull && batch.Count < SearchBatchRows)
                break;

            if (pageIsFull || read >= MaxSearchedRows || Stopwatch.GetElapsedTime(started) >= _searchBudget)
            {
                next = cursor;
                break;
            }
        }

        if (found.Count == 0)
            return new PrintJobSearchResult([], next);

        var ids = found.Select(hit => hit.Id).ToList();
        var facts = await db.PrintJobs.AsNoTracking()
            .Where(job => ids.Contains(job.Id))
            .Select(Facts)
            .ToDictionaryAsync(job => job.Id, timeout.Token);
        var hits = found
            .Where(hit => facts.ContainsKey(hit.Id))
            .Select(hit => new PrintJobSearchHit(facts[hit.Id].ToSummary(), hit.Snippet))
            .ToList();
        return new PrintJobSearchResult(hits, next);
    });

    // The strips that printed with the papercut header as their first line, grouped by subject: the text after the
    // header on its line, or the next line. Two strips are the same papercut when that line is the same, letter case aside.
    // A reprint is not a new papercut.
    public Task<PapercutLedger> PapercutsAsync(CancellationToken cancellationToken) => QueryAsync(async () =>
    {
        using var timeout = Timeout(cancellationToken);
        await using var db = Open();

        // "Title" has no index. The read goes over the newest jobs only, so its cost does not grow with the journal.
        var rows = await db.PrintJobs.AsNoTracking()
            .OrderByDescending(job => job.Id)
            .Take(MaxPapercutJobs)
            .Where(job => job.Result == JobResult.Printed && job.ReprintOf == null && job.Title != null && job.Title.StartsWith(PapercutHeader))
            .OrderByDescending(job => job.Id)
            .Take(MaxPapercutStrips + 1)
            .Join(
                db.PrintJobTexts.AsNoTracking(),
                job => job.Id,
                text => text.JobId,
                (job, text) => new { job.Id, job.CreatedAt, Head = text.Text.Substring(0, PapercutHeadLength) })
            .ToListAsync(timeout.Token);

        // A walk over the id index only: is a job older than the ones that were looked at?
        var hasOlderJobs = await db.PrintJobs.AsNoTracking()
            .OrderByDescending(job => job.Id)
            .Skip(MaxPapercutJobs)
            .AnyAsync(timeout.Token);

        var papercuts = new Dictionary<string, PapercutEntry>(StringComparer.OrdinalIgnoreCase);
        var strips = 0;
        // Newest first: the first strip of a subject gives the text and the id that the ledger shows.
        // The join gives the rows back in no fixed order. The id is a GUID v7: its order is the order in time.
        foreach (var row in rows.OrderByDescending(row => row.Id).Take(MaxPapercutStrips))
        {
            if (PapercutSubject(row.Head) is not { } subject)
                continue;

            strips++;
            var at = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc);
            papercuts[subject] = papercuts.TryGetValue(subject, out var entry)
                ? entry with { Count = entry.Count + 1, FirstAt = at }
                : new PapercutEntry(subject, 1, at, at, row.Id);
        }

        var ledger = papercuts.Values
            .OrderByDescending(entry => entry.Count)
            .ThenByDescending(entry => entry.LastAt)
            .Take(MaxPapercuts)
            .ToList();
        return new PapercutLedger(ledger, strips, hasOlderJobs || rows.Count > MaxPapercutStrips || papercuts.Count > MaxPapercuts);
    });

    // The job with its printed text, for a reader that takes text and no blocks (MCP). Null: no such job.
    // The text of a reprint is the text of its first job. It is cut at PrintJobEntry.MaxSearchTextLength.
    internal async Task<(PrintJobSummary Job, string? Text)?> GetTextAsync(Guid id, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        await using var db = Open();

        var facts = await db.PrintJobs.AsNoTracking().Where(job => job.Id == id).Select(Facts).FirstOrDefaultAsync(timeout.Token);
        if (facts is null)
            return null;

        var textId = facts.ReprintOf ?? id;
        var text = await db.PrintJobTexts.AsNoTracking()
            .Where(row => row.JobId == textId)
            .Select(row => row.Text)
            .FirstOrDefaultAsync(timeout.Token);
        return (facts.ToSummary(), text);
    }

    // The search text of a caller, with no space at its ends. Null: too short or too long.
    public static string? CleanQuery(string? query)
        => query?.Trim() is { Length: >= MinQueryLength and <= MaxQueryLength } text ? text : null;

    // Null: the first line is not the papercut header.
    internal static string? PapercutSubject(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || PapercutHeaderLine().Match(lines[0]) is not { Success: true } header)
            return null;

        // A line of dashes or stars is a rule, not a subject.
        var subject = header.Groups["rest"].Value is { Length: > 0 } rest && rest.Any(char.IsLetterOrDigit)
            ? rest
            : lines.Skip(1).FirstOrDefault(line => line.Any(char.IsLetterOrDigit)) ?? lines[0];
        return PrintJobEntry.CutAtCharacter(string.Join(' ', subject.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), MaxSubjectLength);
    }

    // A piece of the text around the match, on one line, at most MaxSnippetLength characters.
    internal static string Snippet(string text, int matchAt)
    {
        const char Ellipsis = '…';

        var start = Math.Max(0, matchAt - SnippetLead);
        var end = Math.Min(text.Length, start + MaxSnippetLength - 2);
        // No half of a surrogate pair at an end.
        if (start > 0 && char.IsLowSurrogate(text[start]))
            start++;
        if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1]))
            end--;

        var snippet = new StringBuilder(MaxSnippetLength);
        if (start > 0)
            snippet.Append(Ellipsis);

        var afterSpace = true;
        foreach (var character in text.AsSpan(start, end - start))
        {
            var isSpace = char.IsWhiteSpace(character) || char.IsControl(character);
            if (!isSpace)
                snippet.Append(character);
            else if (!afterSpace)
                snippet.Append(' ');
            afterSpace = isSpace;
        }

        if (end < text.Length)
            snippet.Append(Ellipsis);
        return snippet.ToString();
    }

    // False: MaxQueries queries run. The caller that gets true calls EndQuery.
    internal bool TryBeginQuery() => _queryGate.Wait(0);

    internal void EndQuery() => _queryGate.Release();

    private async Task<T> QueryAsync<T>(Func<Task<T>> query)
    {
        if (!TryBeginQuery())
            throw new JournalBusyException();

        try
        {
            return await query();
        }
        finally
        {
            EndQuery();
        }
    }
}
