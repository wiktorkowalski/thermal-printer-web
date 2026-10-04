using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Journal;

// A stored job, ready to print again. Content is null when the journal holds no full copy; NoCopyReason says why.
internal sealed record StoredJob(Guid OriginalId, List<PrintContent>? Content, PrintOptions? Options, string? NoCopyReason);

// Reads the journal for the public job endpoints. Every read has its own short-lived context and a time limit,
// so a reader cannot hold the database against the writer. A list reads PrintJobs only.
// Public on purpose: the controller takes it as an injected parameter. The constructor is internal, so Program.cs builds it.
public sealed class PrintJournalReader
{
    private readonly JournalDatabase _database;
    private readonly PrintJournal _journal;

    internal PrintJournalReader(JournalDatabase database, PrintJournal journal)
    {
        _database = database;
        _journal = journal;
    }

    internal const int DefaultPageSize = 20;
    internal const int MaxPageSize = 50;

    // Fixed texts: they go to the caller.
    internal const string NoBlocksReason = "The job has no stored blocks: it was rejected before the print path got it";
    internal const string NoImageReason = "The job has an image that the journal did not store";
    internal const string UnreadableReason = "The stored job cannot be read";

    private const string CharsMarker = ";chars=";

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    // One reprint at a time: a reprint of an image job reads a request of up to 30 MB, and the call that starts it is a few bytes.
    private readonly SemaphoreSlim _reprintGate = new(1, 1);

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

        var payload = await db.PrintJobPayloads.AsNoTracking()
            .Where(row => row.JobId == id)
            .Select(row => new { row.Blocks, row.Options })
            .FirstOrDefaultAsync(timeout.Token);
        return new PrintJobDetail(facts.ToSummary(), Json(payload?.Blocks), Json(payload?.Options));
    }

    // Null: no such job. The blocks come from the stored job; each picture comes from the stored request of the first job.
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

        // A reprint row has no request of its own: the pictures are in the request of the first job.
        var originalId = job.ReprintOf ?? id;
        var payload = await db.PrintJobPayloads.AsNoTracking()
            .Where(row => row.JobId == id)
            .Select(row => new { row.Blocks, row.Options })
            .FirstOrDefaultAsync(timeout.Token);
        if (payload?.Blocks is null)
            return new StoredJob(originalId, null, null, NoBlocksReason);

        List<PrintContent>? content;
        PrintOptions? options;
        try
        {
            content = JsonSerializer.Deserialize<List<PrintContent>>(payload.Blocks, PrintJobEntry.ApiJson);
            options = payload.Options is null ? null : JsonSerializer.Deserialize<PrintOptions>(payload.Options, PrintJobEntry.ApiJson);
        }
        catch (JsonException)
        {
            return new StoredJob(originalId, null, null, UnreadableReason);
        }

        if (content is null)
            return new StoredJob(originalId, null, null, UnreadableReason);

        if (content.Any(IsStoredImage))
        {
            var request = await db.PrintJobPayloads.AsNoTracking()
                .Where(row => row.JobId == originalId)
                .Select(row => row.Request)
                .FirstOrDefaultAsync(timeout.Token);
            if (request is null || !TryRestoreImages(content, request))
                return new StoredJob(originalId, null, null, NoImageReason);
        }

        return new StoredJob(originalId, content, options, null);
    }

    // Puts the base64 text of each picture back in place of its hash. The request is the JSON body as it came,
    // from HTTP or from MCP: the search is by hash, so the shape of the body does not matter.
    internal static bool TryRestoreImages(List<PrintContent> content, byte[] request)
    {
        var images = content.Where(IsStoredImage).ToList();
        var lengths = new HashSet<int>();
        foreach (var image in images)
        {
            if (TextLength(image!.Content!) is not { } length)
                return false;
            lengths.Add(length);
        }

        var minLength = lengths.Min();
        var wanted = images.Select(image => image!.Content!).ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(request);
            while (found.Count < wanted.Count && reader.Read())
            {
                // An escaped character takes more bytes than one: a shorter value cannot be a picture of the job.
                if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length < minLength)
                    continue;

                var text = reader.GetString()!;
                if (!lengths.Contains(text.Length))
                    continue;

                var hash = PrintJobEntry.ImageHash(text);
                if (wanted.Contains(hash))
                    found[hash] = text;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (found.Count < wanted.Count)
            return false;

        foreach (var image in images)
            image!.Content = found[image.Content!];
        return true;
    }

    // In the stored blocks every image with content holds a hash (PrintJobEntry.BlocksJson).
    private static bool IsStoredImage(PrintContent? block) => block is { Type: ContentType.Image, Content.Length: > 0 };

    // "sha256:<hex>;chars=<n>": n is the length of the base64 text.
    private static int? TextLength(string imageHash)
    {
        var at = imageHash.LastIndexOf(CharsMarker, StringComparison.Ordinal);
        return imageHash.StartsWith(PrintJobEntry.ImageHashPrefix, StringComparison.Ordinal)
            && at > 0
            && int.TryParse(imageHash.AsSpan(at + CharsMarker.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                ? length
                : null;
    }

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
