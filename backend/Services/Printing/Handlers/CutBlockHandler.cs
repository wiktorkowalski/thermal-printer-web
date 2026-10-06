using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class CutBlockHandler : IBlockHandler
{
    public ContentType Type => ContentType.Cut;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        ctx.HasCut = true;
        CutFeed.AddLines(ctx);
        ctx.AddPaper(PaperLength.CutDots(CutFeed.MotionUnits));
        ctx.Add(item.PartialCut == true
            ? ctx.Emitter.PartialCutAfterFeed(CutFeed.MotionUnits)
            : ctx.Emitter.FullCutAfterFeed(CutFeed.MotionUnits));
        return Task.CompletedTask;
    }
}
