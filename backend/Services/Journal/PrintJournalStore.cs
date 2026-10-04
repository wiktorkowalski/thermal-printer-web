using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ThermalPrinterWeb.Services.Journal;

// The journal database file. Every reader and writer gets its context here.
// Not AddDbContextFactory: that reads the settings when the host starts, and a bad journal setting must not stop the app.
internal sealed class JournalDatabase(IOptions<JournalOptions> options, IHostEnvironment environment)
{
    private readonly Lazy<DbContextOptions<JournalDbContext>> _contextOptions = new(() =>
        new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlite(JournalDbContext.ConnectionString(options.Value.DatabasePath(environment.ContentRootPath)))
            .Options);

    // Throws when the journal settings do not bind.
    public string Path => options.Value.DatabasePath(environment.ContentRootPath);

    public JournalDbContext CreateContext() => new(_contextOptions.Value);
}

// Where the journal rows go. The tests replace it with a store that throws or hangs.
internal interface IPrintJournalStore
{
    // Creates the directory and the database and brings the schema up to date. Returns the database path.
    Task<string> OpenAsync(CancellationToken cancellationToken);

    // Throws JournalFullException when a size limit stops the write.
    Task AddAsync(PrintJob job, CancellationToken cancellationToken);

    // Deletes the rows of the filter in one transaction, when their number is at most maxRows and, with confirmRows, equal to it.
    // Else it deletes nothing. The answer holds the rows that fit and says whether they are deleted.
    Task<JobSelection> DeleteAsync(JobDeleteFilter filter, int? confirmRows, int maxRows, CancellationToken cancellationToken);

    // Gives the space of deleted rows back to the disk: the size limit of the journal is the size of its files.
    Task CompactAsync(CancellationToken cancellationToken);
}

// Not a fault of the storage: a limit from JournalOptions is reached. The message names the limit and holds no job content.
internal sealed class JournalFullException(string message) : Exception(message);

internal sealed class SqlitePrintJournalStore(JournalDatabase database, IOptions<JournalOptions> options) : IPrintJournalStore
{
    private const string WriteAheadLogSuffix = "-wal";

    // The journal holds what was printed: only the user of the app reads it.
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode DatabaseFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public async Task<string> OpenAsync(CancellationToken cancellationToken)
    {
        var path = database.Path;
        CreateFile(path);

        await using var db = database.CreateContext();
        await db.Database.MigrateAsync(cancellationToken);
        // Stays set in the database file. A reader over ssh does not block the writer.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        return path;
    }

    public async Task AddAsync(PrintJob job, CancellationToken cancellationToken)
    {
        CheckSpace();

        await using var db = database.CreateContext();
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
    }

    // The foreign keys of PrintJobPayloads and PrintJobTexts delete their rows with the job (cascade, in the database).
    public async Task<JobSelection> DeleteAsync(JobDeleteFilter filter, int? confirmRows, int maxRows, CancellationToken cancellationToken)
    {
        await using var db = database.CreateContext();
        // The count and the delete see the same rows.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var selection = await JobSelection.ReadAsync(db, filter, cancellationToken);
        if (selection.Rows == 0 || selection.Rows > maxRows || (confirmRows is { } confirmed && confirmed != selection.Rows))
            return selection;

        await JobSelection.WithReprints(db, filter).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return selection with { Deleted = true };
    }

    // A delete leaves free pages in the file; the file keeps its size. VACUUM writes the database again without them,
    // through the write-ahead log, so the log is cut after it. It needs free disk space of about twice the database.
    public async Task CompactAsync(CancellationToken cancellationToken)
    {
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("VACUUM;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
    }

    // SQLite gives the write-ahead log and the shared-memory file the mode of the database file.
    private static void CreateFile(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        // An existing directory keeps its mode: it can be a volume that the host owns.
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory, DirectoryMode);

        if (!File.Exists(path))
        {
            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = DatabaseFileMode
            });
        }
    }

    // The size of the new row is not counted: a small job after a large one must not turn "full" off and on.
    // So the database can pass its limit by one row, at most the request body limit plus the printer data limit.
    private void CheckSpace()
    {
        var path = database.Path;
        var limits = options.Value;

        var databaseBytes = FileLength(path) + FileLength(path + WriteAheadLogSuffix);
        if (databaseBytes >= limits.MaxDatabaseBytes)
        {
            throw new JournalFullException(
                $"the database is {databaseBytes} bytes, the limit Journal:MaxDatabaseBytes is {limits.MaxDatabaseBytes}");
        }

        var freeBytes = new DriveInfo(path).AvailableFreeSpace;
        if (freeBytes < limits.MinFreeBytes)
        {
            throw new JournalFullException(
                $"the disk has {freeBytes} bytes free, the limit Journal:MinFreeBytes is {limits.MinFreeBytes}");
        }
    }

    private static long FileLength(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }
}
