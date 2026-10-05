using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

// Bytes only: no paper and no printed text. PrinterService limits the Signal blocks of one document.
internal sealed class SignalBlockHandler : IBlockHandler
{
    public ContentType Type => ContentType.Signal;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        var options = item.SignalOptions;
        var count = InRange("signalOptions.count", options?.Count);
        var duration = InRange("signalOptions.duration", options?.Duration);

        ctx.Add(SignalCommand.Build(options?.Mode ?? SignalMode.Sound, count, duration));
        return Task.CompletedTask;
    }

    // A field that is left out is the smallest value: one short signal.
    private static int InRange(string field, int? value)
    {
        var number = value ?? SignalCommand.Min;
        return number is >= SignalCommand.Min and <= SignalCommand.Max
            ? number
            : throw PrintContentException.OutOfRange(field, number, SignalCommand.Min, SignalCommand.Max);
    }
}
