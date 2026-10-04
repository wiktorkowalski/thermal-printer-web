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
        CheckSpace(job);

        await using var db = database.CreateContext();
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
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

    private void CheckSpace(PrintJob job)
    {
        var path = database.Path;
        var limits = options.Value;

        var rowBytes = PrintJournal.PayloadBytes(job);
        var databaseBytes = FileLength(path) + FileLength(path + WriteAheadLogSuffix);
        if (databaseBytes + rowBytes > limits.MaxDatabaseBytes)
        {
            throw new JournalFullException(
                $"the database is {databaseBytes} bytes, the limit Journal:MaxDatabaseBytes is {limits.MaxDatabaseBytes}");
        }

        var freeBytes = new DriveInfo(path).AvailableFreeSpace;
        if (freeBytes - rowBytes < limits.MinFreeBytes)
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
