using System.Collections.Concurrent;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;

namespace ThermalPrinterWeb.Tests.Support;

// The printer of a test: keeps each job, answers Result, sends nothing.
public sealed class RecordingPrinter : IPrinterService
{
    public ConcurrentQueue<(List<PrintContent> Content, PrintOptions? Options)> Jobs { get; } = [];
    public PrintResult Result { get; set; } = PrintResult.Ok;

    public Task<PrintResult> PrintAsync(List<PrintContent> content, PrintOptions? options = null)
    {
        Jobs.Enqueue((content, options));
        return Task.FromResult(Result);
    }

    public Task<PrinterStatus> GetStatusAsync() => throw new NotSupportedException();
    public ConcurrentQueue<(int Count, int Duration, SignalMode Mode)> Beeps { get; } = [];

    public Task<bool> BeepAsync(int count, int duration, SignalMode mode = SignalMode.Sound)
    {
        Beeps.Enqueue((count, duration, mode));
        return Task.FromResult(true);
    }
}
