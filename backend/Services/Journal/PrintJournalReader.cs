using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Journal;

// A stored job, ready to print again. Content is null when the journal holds no full copy; NoCopyReason says why.
internal sealed record StoredJob(Guid OriginalId, List<PrintContent>? Content, PrintOptions? Options, string? NoCopyReason)
{
    public static StoredJob NoCopy(Guid originalId, string reason) => new(originalId, null, null, reason);
}

// Why a journal read gave no value. The texts are fixed and go to the caller: no detail of the storage.
public sealed record JournalFault(string Error, string Type)
{
    public static readonly JournalFault Off = new("The print journal is off", PrintResponse.JournalOffType);

    // Not open yet, or the read failed: a later call can pass.
    public static readonly JournalFault Unavailable = new("The print journal is not available", PrintResponse.JournalType);

    // Too many statistics, search or ledger reads run (PrintJournalReader.MaxQueries).
    public static readonly JournalFault Busy = new("Server busy: other journal queries run. Send it again in a few seconds.", PrintResponse.BusyType);
}

// Reads the journal for the public job endpoints and the MCP tools. Every read has its own short-lived context
// and a time limit, so a reader cannot hold the database against the writer. A list reads PrintJobs only.
// Public on purpose: the controller and the MCP tools take it as an injected parameter.
// The constructor is internal, so Program.cs builds it. The statistics, the search and the ledger are in PrintJournalReader.Queries.cs.
public sealed partial class PrintJournalReader
{
    internal const int DefaultPageSize = 20;
    internal const int MaxPageSize = 50;

    // Fixed texts: they go to the caller.
    internal const string NoBlocksReason = "The job has no stored blocks: it was rejected before the print path got it";
    internal const string NoImageReason = "The job has an image that the journal did not store";
    internal const string UnreadableReason = "The stored job cannot be read";

    // Fixed texts for the log: what a failed read was for.
    internal const string ListRead = "the job list";
    internal const string JobRead = "a job";

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    // One reprint at a time: a reprint of an image job reads a request of up to 30 MB, and the call that starts it is a few bytes.
    private readonly SemaphoreSlim _reprintGate = new(1, 1);

    private readonly JournalDatabase _database;
    private readonly PrintJournal _journal;
    private readonly TimeSpan _searchBudget;

    internal PrintJournalReader(JournalDatabase database, PrintJournal journal, TimeSpan? searchBudget = null)
    {
        _database = database;
        _journal = journal;
        _searchBudget = searchBudget ?? DefaultSearchBudget;
    }

    // One journal read for a caller. A fault in place of the value: the journal is off, not open yet, busy, or the read failed.
    // The log gets one Warning for a failed read: "what" is a fixed text, and the job id when the read is for one job.
    public async Task<(T? Value, JournalFault? Fault)> TryReadAsync<T>(
        Func<CancellationToken, Task<T>> read, ILogger logger, string what, Guid? jobId, CancellationToken cancellationToken)
    {
        if (!IsOn)
            return (default, JournalFault.Off);

        try
        {
            return (await read(cancellationToken), null);
        }
        catch (JournalBusyException)
        {
            return (default, JournalFault.Busy);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (jobId is null)
                logger.LogWarning(ex, "Journal read failed: {What}", what);
            else
                logger.LogWarning(ex, "Journal read failed: job {JobId}", jobId);
            return (default, JournalFault.Unavailable);
        }
    }

    private static readonly Expression<Func<PrintJob, JobFacts>> Facts = job => new JobFacts(
        job.Id, job.CreatedAt, job.Transport, job.Source, job.Result, job.Error, job.Title, job.BlockCount, job.PaperDots, job.ReprintOf);

    // The columns a summary is made of. No other column of PrintJobs leaves the database on a read.
    private sealed record JobFacts(
        Guid Id, DateTime CreatedAt, string Transport, string? Source, JobResult Result, string? Error, string? Title,
        int? BlockCount, int? PaperDots, Guid? ReprintOf)
    {
        public PrintJobSummary ToSummary() => new(
            Id,
            // SQLite gives the time back with no kind; without it the JSON has no "Z".
            DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc),
            Transport,
            Source,
            Result.ToString(),
            Error,
            Title,
            BlockCount,
            PaperDots,
            ReprintOf,
            CanReprint: BlockCount is not null);
    }

    // The form the list gives out. Any other text is not an id.
    public static bool TryParseId(string text, out Guid id) => Guid.TryParseExact(text, "D", out id);

    // The "before" value of a caller. No value is no cursor: the first page. False: the value is not an id.
    public static bool TryParseCursor(string? before, out Guid? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(before))
            return true;
        if (!TryParseId(before, out var id))
            return false;

        cursor = id;
        return true;
    }

    // False: the journal is off, and no read is tried. True does not say that the database is open yet.
    public bool IsOn => _journal.IsOn;

    // False: another reprint runs. The caller that gets true calls EndReprint.
    public bool TryBeginReprint() => _reprintGate.Wait(0);

    public void EndReprint() => _reprintGate.Release();

    // Newest first. The id is a GUID v7, so its order is the order in time; "before" is the last id of the page before.
    public async Task<PrintJobList> ListAsync(Guid? before, int limit, bool printedOnly, CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(limit, 1, MaxPageSize);
        using var timeout = Timeout(cancellationToken);
        await using var db = Open();

        var jobs = db.PrintJobs.AsNoTracking();
        if (printedOnly)
            jobs = jobs.Where(job => job.Result == JobResult.Printed);
        if (before is { } cursor)
            jobs = jobs.Where(job => job.Id.CompareTo(cursor) < 0);

        // One row more than the page: it tells whether a next page exists.
        var rows = await jobs.OrderByDescending(job => job.Id).Take(pageSize + 1).Select(Facts).ToListAsync(timeout.Token);
        var page = rows.Take(pageSize).Select(row => row.ToSummary()).ToList();
        return new PrintJobList(page, rows.Count > pageSize ? page[^1].Id : null);
    }

    // Null: no such job.
    public async Task<PrintJobDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        await using var db = Open();

        var facts = await db.PrintJobs.AsNoTracking().Where(job => job.Id == id).Select(Facts).FirstOrDefaultAsync(timeout.Token);
        if (facts is null)
            return null;

        var copy = await CopyAsync(db, facts.ReprintOf ?? id, timeout.Token);
        return new PrintJobDetail(facts.ToSummary(), Json(copy?.Blocks), Json(copy?.Options));
    }

    // Null: no such job. The blocks and each picture come from the first job: a reprint row holds a reference only.
    internal async Task<StoredJob?> LoadForReprintAsync(Guid id, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        await using var db = Open();

        var job = await db.PrintJobs.AsNoTracking()
            .Where(row => row.Id == id)
            .Select(row => new { row.ReprintOf })
            .FirstOrDefaultAsync(timeout.Token);
        if (job is null)
            return null;

        var originalId = job.ReprintOf ?? id;
        var copy = await CopyAsync(db, originalId, timeout.Token);
        if (copy?.Blocks is null)
            return StoredJob.NoCopy(originalId, NoBlocksReason);

        List<PrintContent> content;
        PrintOptions? options;
        try
        {
            content = JsonSerializer.Deserialize<List<PrintContent>>(copy.Blocks, PrintJobEntry.ApiJson) ?? throw new JsonException();
            options = copy.Options is null ? null : JsonSerializer.Deserialize<PrintOptions>(copy.Options, PrintJobEntry.ApiJson);
        }
        catch (JsonException)
        {
            return StoredJob.NoCopy(originalId, UnreadableReason);
        }

        if (content.Any(IsStoredImage))
        {
            var request = await db.PrintJobPayloads.AsNoTracking()
                .Where(row => row.JobId == originalId)
                .Select(row => row.Request)
                .FirstOrDefaultAsync(timeout.Token);
            if (request is null || !TryRestoreImages(content, request))
                return StoredJob.NoCopy(originalId, NoImageReason);
        }

        return new StoredJob(originalId, content, options, null);
    }

    // The text values only: the entity is never loaded, its blobs are up to 30 MB.
    private static Task<JobCopy?> CopyAsync(JournalDbContext db, Guid jobId, CancellationToken cancellationToken)
        => db.PrintJobPayloads.AsNoTracking()
            .Where(row => row.JobId == jobId)
            .Select(row => new JobCopy(row.Blocks, row.Options))
            .FirstOrDefaultAsync(cancellationToken);

    private sealed record JobCopy(string? Blocks, string? Options);

    // Puts the base64 text of each picture back in place of its hash. The request is the JSON body as it came,
    // from HTTP or from MCP: the search is by hash, so the shape of the body does not matter.
    internal static bool TryRestoreImages(List<PrintContent> content, byte[] request)
    {
        var images = content.Where(IsStoredImage).ToList();
        var wanted = images.Select(image => image.Content!).ToHashSet(StringComparer.Ordinal);
        var lengths = new HashSet<int>();
        foreach (var hash in wanted)
        {
            if (PrintJobEntry.ImageTextLength(hash) is not { } length)
                return false;
            lengths.Add(length);
        }

        var minLength = lengths.Min();
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(request);
            while (found.Count < wanted.Count && reader.Read())
            {
                // An escaped character takes more bytes than one: a shorter value cannot be a picture of the job.
                if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length < minLength)
                    continue;

                // A picture is up to 22 MB of text: no copy of a value before its hash says that the job needs it.
                string? text = null;
                string hash;
                if (reader.ValueIsEscaped)
                {
                    text = reader.GetString()!;
                    if (!lengths.Contains(text.Length))
                        continue;
                    hash = PrintJobEntry.ImageHash(text);
                }
                else
                {
                    if (!lengths.Contains(Encoding.UTF8.GetCharCount(reader.ValueSpan)))
                        continue;
                    hash = PrintJobEntry.ImageHashOfUtf8(reader.ValueSpan);
                }

                if (wanted.Contains(hash))
                    found.TryAdd(hash, text ?? reader.GetString()!);
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (found.Count < wanted.Count)
            return false;

        foreach (var image in images)
            image.Content = found[image.Content!];
        return true;
    }

    // In the stored blocks every image with content holds a hash (PrintJobEntry.BlocksJson).
    // A null entry is not an image; the print path refuses it later.
    private static bool IsStoredImage(PrintContent block) => block is { Type: ContentType.Image, Content.Length: > 0 };

    private static JsonElement? Json(string? stored)
    {
        if (stored is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(stored);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private JournalDbContext Open()
    {
        var db = _database.CreateContext();
        db.Database.SetCommandTimeout(ReadTimeout);
        return db;
    }

    private static CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        return timeout;
    }
}
