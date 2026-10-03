using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class LineFeedBlockHandler : IBlockHandler
{
    // 100 lines = 2900 dots = 36 cm of blank paper.
    internal const int MaxLines = 100;

    public ContentType Type => ContentType.LineFeed;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        var lines = item.Lines ?? 1;
        if (lines > MaxLines)
            throw new PrintContentException($"lines {lines} is over the limit of {MaxLines}");

        for (int i = 0; i < lines; i++)
            ctx.Add(ctx.Emitter.PrintLine(string.Empty));
        return Task.CompletedTask;
    }
}
