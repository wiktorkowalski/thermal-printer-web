using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class LineFeedBlockHandler : IBlockHandler
{
    public ContentType Type => ContentType.LineFeed;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        for (int i = 0; i < (item.Lines ?? 1); i++)
            ctx.Add(ctx.Emitter.PrintLine(string.Empty));
        return Task.CompletedTask;
    }
}
