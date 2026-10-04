using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace ThermalPrinterWeb.Services.Journal;

// A delegate, so a test host answers for itself: CI runs the tests inside a container, on its layer.
internal delegate bool ContainerLayerCheck(string directory);

// Stores every print job. One background writer, so no print waits for the storage:
// a request hands its entry over and ends. A fault here costs a journal row, never a print.
internal sealed class PrintJournal(
    IPrintJournalStore store,
    IOptions<JournalOptions> options,
    ContainerLayerCheck isInContainerLayer,
    ILogger<PrintJournal> logger) : BackgroundService
{
    // Entries that wait for the writer. Past a limit a new entry is dropped: a slow disk must not fill the memory.
    internal const int MaxPendingJobs = 16;
    internal const long MaxPendingBytes = 64L * 1024 * 1024;

    private const string ContainerVariable = "DOTNET_RUNNING_IN_CONTAINER";
    private const string MountInfoPath = "/proc/self/mountinfo";

    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);

    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _pendingLock = new();
    private int _pendingJobs;
    private long _pendingBytes;

    private volatile bool _off;
    private bool _full;

    // A row or a barrier. A barrier is done when the writer gets to it.
    private readonly record struct Work(PrintJob? Job, TaskCompletionSource? Barrier);

    // True from the start: a job that comes before the database is open waits in the queue.
    public bool IsOn => !_off;

    public void Add(PrintJob job)
    {
        if (_off)
            return;

        var bytes = job.Payload.LargeBytes();
        lock (_pendingLock)
        {
            // One entry always fits: a photo job alone can be over half the byte limit.
            var fits = _pendingJobs < MaxPendingJobs && (_pendingJobs == 0 || _pendingBytes + bytes <= MaxPendingBytes);
            if (fits)
            {
                _pendingJobs++;
                _pendingBytes += bytes;
            }
            else
            {
                logger.LogWarning(
                    "Journal is behind: job {JobId} is not stored ({PendingJobs} jobs and {PendingBytes} bytes wait for the writer)",
                    job.Id, _pendingJobs, _pendingBytes);
                return;
            }
        }

        // False after the host stopped the writer.
        if (!_queue.Writer.TryWrite(new Work(job, null)))
            Release(bytes);
    }

    // Done when every entry added before the call is stored or given up. For tests.
    internal Task DrainAsync()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _queue.Writer.TryWrite(new Work(null, barrier)) ? barrier.Task : Task.CompletedTask;
    }

    // The host waits for the entries in the queue, up to its shutdown timeout.
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    // The stopping token is not used: StopAsync ends the queue, and the loop ends after the last entry.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // .NET 10 runs this method off the startup path: the app listens while the database opens.
        _off = !await OpenAsync();

        await foreach (var work in _queue.Reader.ReadAllAsync(CancellationToken.None))
        {
            if (work.Job is { } job)
            {
                if (!_off)
                    await WriteAsync(job);
                Release(job.Payload.LargeBytes());
            }

            work.Barrier?.SetResult();
        }
    }

    // One line states the journal state. No exception leaves this method: it would stop the host.
    private async Task<bool> OpenAsync()
    {
        try
        {
            var settings = options.Value;
            if (settings.IsOff)
            {
                logger.LogInformation("Journal: off (Journal:DataPath is empty)");
                return false;
            }

            if (settings.Error() is { } error)
            {
                logger.LogWarning("Journal is off: {Reason}. Prints are not stored.", error);
                return false;
            }

            using var timeout = new CancellationTokenSource(OpenTimeout);
            var path = await store.OpenAsync(timeout.Token).WaitAsync(OpenTimeout);
            logger.LogInformation("Journal: {Path}", path);

            var directory = Path.GetDirectoryName(path)!;
            if (isInContainerLayer(directory))
            {
                logger.LogWarning(
                    "Journal directory {Directory} is not a volume: the journal is lost when the container is replaced. Mount a volume there.",
                    directory);
            }

            return true;
        }
        catch (Exception ex)
        {
            // A setting that does not bind, a directory that cannot be made, a database that does not open.
            logger.LogWarning(ex, "Journal is off: it did not start. Prints are not stored.");
            return false;
        }
    }

    private async Task WriteAsync(PrintJob job)
    {
        try
        {
            // The settings bind: OpenAsync read them.
            var writeTimeout = options.Value.WriteTimeout;
            using var timeout = new CancellationTokenSource(writeTimeout);
            // WaitAsync: a store that ignores the token must not stop the writer.
            await store.AddAsync(job, timeout.Token).WaitAsync(writeTimeout);

            if (_full)
            {
                _full = false;
                logger.LogInformation("Journal has space again: prints are stored");
            }
        }
        catch (JournalFullException ex)
        {
            // One line for the whole time the journal is full, not one per job.
            if (!_full)
            {
                _full = true;
                logger.LogWarning("Journal is full: {Reason}. Prints are not stored until there is space.", ex.Message);
            }
        }
        catch (Exception ex)
        {
            // No job content: the exception holds none (EF Core logs no parameter values).
            logger.LogWarning(ex, "Journal write failed: job {JobId} is not stored", job.Id);
        }
    }

    private void Release(long bytes)
    {
        lock (_pendingLock)
        {
            _pendingJobs--;
            _pendingBytes -= bytes;
        }
    }

    // True: the directory is inside a container and on no volume, so a new container starts with an empty journal.
    internal static bool IsInContainerLayer(string directory)
    {
        if (!OperatingSystem.IsLinux()
            || !string.Equals(Environment.GetEnvironmentVariable(ContainerVariable), "true", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(MountInfoPath))
        {
            return false;
        }

        return !IsOnMount(File.ReadLines(MountInfoPath), directory);
    }

    // A line of /proc/self/mountinfo: "id parent major:minor root mount-point options ...".
    // The root mount is the container layer. Any other mount at or above the directory is a volume.
    internal static bool IsOnMount(IEnumerable<string> mountInfo, string directory)
    {
        const int MountPointField = 4;

        return mountInfo
            .Select(line => line.Split(' '))
            .Where(fields => fields.Length > MountPointField)
            .Select(fields => fields[MountPointField])
            .Any(mountPoint => mountPoint != "/"
                && (directory == mountPoint || directory.StartsWith(mountPoint + "/", StringComparison.Ordinal)));
    }
}
