using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ThermalPrinterWeb.Tests.Support;

// Log capture for a host: every category goes to one list.
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);
    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Enqueue((logLevel, category, formatter(state, exception) + exception));
    }
}

// Log capture for one object that a test builds by hand.
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception) + exception));
}
