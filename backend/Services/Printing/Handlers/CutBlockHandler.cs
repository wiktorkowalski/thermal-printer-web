using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class CutBlockHandler : IBlockHandler
{
    public ContentType Type => ContentType.Cut;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        ctx.HasCut = true;
        var feedLines = ctx.Options?.FeedLinesAfterPrint ?? 3;
        ctx.Add(item.PartialCut == true
            ? ctx.Emitter.PartialCutAfterFeed(feedLines)
            : ctx.Emitter.FullCutAfterFeed(feedLines));
        return Task.CompletedTask;
    }
}
