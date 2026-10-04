using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Journal;

// What the caller facts are once a print entry point knows them. PrintJobLog sets it.
internal sealed record PrintJobOutcome(
    string Transport,
    string? Source,
    PrintResult Result,
    List<PrintContent>? Content,
    PrintOptions? Options);

// The facts of one journaled request. PrintJournalMiddleware opens it; the code below it
// fills it through Current. No trace is open outside a journaled request, so every writer uses "?.".
internal sealed class PrintJobTrace
{
    // A job logs under 10 lines. The limits stop a fault that logs in a loop.
    internal const int MaxLogLines = 200;
    internal const int MaxLogLineLength = 4000;

    private static readonly AsyncLocal<PrintJobTrace?> Ambient = new();

    private readonly List<string> _logLines = [];

    public static PrintJobTrace? Current => Ambient.Value;

    // Time-ordered, and not guessable: issue #51 puts the id in a URL.
    public Guid Id { get; } = Guid.CreateVersion7();

    public PrintJobOutcome? Outcome { get; set; }

    // Set by the reprint endpoint: the job that was first sent.
    public Guid? ReprintOf { get; set; }

    // Set by PrinterService.
    public byte[]? Bytes { get; set; }
    public int? PaperDots { get; set; }
    public PrinterStatus? Status { get; set; }

    // A fault PrinterService handled, or one that left the endpoint (PrintJournalMiddleware).
    public Exception? Exception { get; set; }

    public static PrintJobTrace Begin()
    {
        var trace = new PrintJobTrace();
        Ambient.Value = trace;
        return trace;
    }

    public static void End() => Ambient.Value = null;

    public void AddLogLine(string line)
    {
        lock (_logLines)
        {
            if (_logLines.Count < MaxLogLines)
                _logLines.Add(line.Length > MaxLogLineLength ? line[..MaxLogLineLength] : line);
        }
    }

    public string[] LogLines()
    {
        lock (_logLines)
            return [.. _logLines];
    }
}

// Copies each log line of a journaled request into its trace. Outside such a request it does nothing.
internal sealed class PrintJobTraceLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && PrintJobTrace.Current is not null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None || PrintJobTrace.Current is not { } trace)
                return;

            var line = $"{DateTime.UtcNow:O} {logLevel} {category}: {formatter(state, exception)}";
            trace.AddLogLine(exception is null ? line : $"{line}{Environment.NewLine}{exception}");
        }
    }
}
