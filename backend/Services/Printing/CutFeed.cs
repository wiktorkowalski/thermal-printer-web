using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Services.Printing;

// The paper feed before a cut, for a Cut block and for the auto-cut.
internal static class CutFeed
{
    // The n of the cut command GS V m n: a feed in motion units, under 1 mm. Every cut sends it.
    // With it alone the cutter goes through the last printed line (issue #31, row F1).
    internal const int MotionUnits = 3;

    // The empty lines before a cut of a job that does not send options.feedLinesAfterPrint (owner decision of 2026-10-06):
    // the one that is needed plus two of margin. The house style ends with a LineFeed block of this many lines (SimpleNote.FeedLines).
    internal const int DefaultLines = 3;

    private const byte LineFeed = 0x0A;

    // The feed as LF bytes, like a LineFeed block: LF is the feed that is read from paper on this printer (issue #31),
    // and the n of GS V holds 255 dots at most, under 9 lines. A line has the line spacing of the job; each block sets the text size back to 1 x 1.
    // Over the paper limit it throws PrintContentException.
    public static void AddLines(BlockContext ctx)
    {
        var lines = Lines(ctx.Options?.FeedLinesAfterPrint, ctx.TrailingFeedLines);
        if (lines <= 0)
            return;

        ctx.AddPaper(lines * PaperLength.LineDots(ctx.LineSpacing));
        var feed = new byte[lines];
        feed.AsSpan().Fill(LineFeed);
        ctx.Add(feed);
    }

    // A number that the caller sent: that many lines, whatever the content.
    // Not sent: the lines that the content lacks for DefaultLines empty lines before the cut.
    internal static int Lines(int? sent, int trailingFeedLines)
        => sent ?? Math.Max(0, DefaultLines - trailingFeedLines);

    // The empty lines at the end of the content so far: the lines of the LineFeed blocks after the last block of another type.
    // By type, not by paper: a block with empty content that prints nothing ends the count too.
    // A Signal and a CodePage block never move paper, so they keep the count. An empty line inside a Text block is not counted.
    internal static int TrailingLinesAfter(PrintContent block, int trailingFeedLines) => block.Type switch
    {
        ContentType.LineFeed => trailingFeedLines + LineFeedBlockHandler.LinesOf(block),
        ContentType.Signal or ContentType.CodePage => trailingFeedLines,
        _ => 0
    };
}
