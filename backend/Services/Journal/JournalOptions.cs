namespace ThermalPrinterWeb.Services.Journal;

internal sealed class JournalOptions
{
    public const string SectionName = "Journal";

    // The name the other homelab apps use for the same setting. Journal:DataPath wins over it.
    public const string DataPathVariable = "DATA_PATH";

    public const string DefaultDataPath = "data";
    public const string DatabaseFileName = "journal.db";

    // One text job is about 5 KB; one photo job can be 30 MB. No row is deleted, so these two stop the writes.
    public const long DefaultMaxDatabaseBytes = 1024L * 1024 * 1024;
    public const long DefaultMinFreeBytes = 512L * 1024 * 1024;

    private static readonly TimeSpan MaxWriteTimeout = TimeSpan.FromMinutes(1);

    // A directory. Relative: under the content root (/app in the container). Empty: the journal is off.
    public string DataPath { get; set; } = DefaultDataPath;

    // The database file with its write-ahead log. At this size the journal stops; prints go on.
    public long MaxDatabaseBytes { get; set; } = DefaultMaxDatabaseBytes;

    // Free space on the disk of the journal. Below it the journal stops; prints go on.
    public long MinFreeBytes { get; set; } = DefaultMinFreeBytes;

    // One write. A write that takes longer is given up.
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool IsOff => string.IsNullOrWhiteSpace(DataPath);

    // Null: the settings are valid. A bad setting turns the journal off; it does not stop the app.
    public string? Error()
    {
        if (MaxDatabaseBytes <= 0)
            return $"Journal:MaxDatabaseBytes {MaxDatabaseBytes} must be above 0";
        if (MinFreeBytes < 0)
            return $"Journal:MinFreeBytes {MinFreeBytes} must not be below 0";
        // "3" binds as 3 days.
        if (WriteTimeout <= TimeSpan.Zero || WriteTimeout > MaxWriteTimeout)
            return $"Journal:WriteTimeout {WriteTimeout} is outside the range 00:00:00 to {MaxWriteTimeout}. Use the form hh:mm:ss.";

        return null;
    }

    public string DatabasePath(string contentRoot)
        => Path.GetFullPath(Path.Combine(contentRoot, DataPath.Trim(), DatabaseFileName));
}
