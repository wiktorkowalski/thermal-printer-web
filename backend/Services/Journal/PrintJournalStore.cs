using System.Globalization;
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

    // Deletes these jobs and their reprint rows in one transaction. Returns the number of deleted rows.
    // Null: one of the jobs is gone, and nothing is deleted.
    Task<int?> DeleteAsync(IReadOnlyList<Guid> jobIds, CancellationToken cancellationToken);

    // Gives the space of deleted rows back to the disk: the size limit of the journal is the size of its files.
    // SQLite cannot stop it, so the token is for the wait before it only.
    Task CompactAsync(CancellationToken cancellationToken);
}

// Not a fault of the storage: a limit from JournalOptions is reached. The message names the limit and holds no job content.
internal sealed class JournalFullException(string message) : Exception(message);

internal sealed class SqlitePrintJournalStore(
    JournalDatabase database, IOptions<JournalOptions> options, ILogger<SqlitePrintJournalStore> logger) : IPrintJournalStore
{
    private const string WriteAheadLogSuffix = "-wal";
    private const string CutWriteAheadLog = "PRAGMA wal_checkpoint(TRUNCATE);";
    private const string SetIncrementalAutoVacuum = "PRAGMA auto_vacuum=INCREMENTAL;";

    // The value of "PRAGMA auto_vacuum" for the mode INCREMENTAL.
    private const long IncrementalAutoVacuum = 2;

    // The journal holds what was printed: only the user of the app reads it.
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode DatabaseFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public async Task<string> OpenAsync(CancellationToken cancellationToken)
    {
        var path = database.Path;
        CreateFile(path);

        await using var db = database.CreateContext();
        // One connection for all of it: the auto_vacuum setting of a file with tables takes effect in the VACUUM of the same connection.
        await db.Database.OpenConnectionAsync(cancellationToken);
        // Before the first table: a new file is made in this mode. It lets a delete give its pages back (CompactAsync).
        await db.Database.ExecuteSqlRawAsync(SetIncrementalAutoVacuum, cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
        // Stays set in the database file. A reader over ssh does not block the writer.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);

        try
        {
            // A file from before this mode: one VACUUM writes it again in the mode. Once per file.
            // It needs free disk space of about the size of the file.
            if (await AutoVacuumModeAsync(db, cancellationToken) != IncrementalAutoVacuum)
            {
                await db.Database.ExecuteSqlRawAsync(SetIncrementalAutoVacuum, cancellationToken);
                await db.Database.ExecuteSqlRawAsync("VACUUM;", cancellationToken);
                await db.Database.ExecuteSqlRawAsync(CutWriteAheadLog, cancellationToken);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The journal works without the mode; the next start tries again.
            logger.LogWarning(ex, "Journal: the database file did not get the auto_vacuum mode. A delete does not make the file smaller.");
        }

        return path;
    }

    private static async Task<long> AutoVacuumModeAsync(JournalDbContext db, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA auto_vacuum;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task AddAsync(PrintJob job, CancellationToken cancellationToken)
    {
        CheckSpace();

        await using var db = database.CreateContext();
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
    }

    // The foreign keys of PrintJobPayloads and PrintJobTexts delete their rows with the job (cascade, in the database).
    // No space check: a delete must work while the journal is full.
    public async Task<int?> DeleteAsync(IReadOnlyList<Guid> jobIds, CancellationToken cancellationToken)
    {
        await using var db = database.CreateContext();
        // The check and the delete see the same rows.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await db.PrintJobs.CountAsync(job => jobIds.Contains(job.Id), cancellationToken) != jobIds.Count)
            return null;

        // ReprintOf has no index: this reads the small PrintJobs rows only.
        var rows = await db.PrintJobs
            .Where(job => jobIds.Contains(job.Id) || (job.ReprintOf != null && jobIds.Contains(job.ReprintOf.Value)))
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    // A delete leaves free pages in the file; the file keeps its size. This takes the free pages off the end of the file:
    // the work is in proportion to the deleted rows, not to the database. The write-ahead log is cut after it.
    public async Task CompactAsync(CancellationToken cancellationToken)
    {
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync(CutWriteAheadLog, cancellationToken);
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
