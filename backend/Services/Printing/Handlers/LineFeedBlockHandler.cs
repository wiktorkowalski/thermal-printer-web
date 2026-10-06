using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class LineFeedBlockHandler : IBlockHandler
{
    // 100 lines = 2900 dots = 36 cm of blank paper.
    internal const int MaxLines = 100;

    public ContentType Type => ContentType.LineFeed;

    // The lines that the block feeds: one when the field is left out, none for a negative number.
    internal static int LinesOf(PrintContent item) => Math.Max(item.Lines ?? 1, 0);

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        var lines = LinesOf(item);
        if (lines > MaxLines)
            throw PrintContentException.OverLimit("lines", lines, MaxLines);

        ctx.AddPaper(lines * PaperLength.LineDots(ctx.LineSpacing));
        for (var i = 0; i < lines; i++)
            ctx.Add(ctx.Emitter.PrintLine(string.Empty));
        return Task.CompletedTask;
    }
}
