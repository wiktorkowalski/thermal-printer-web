namespace ThermalPrinterWeb.Services.Printing;

// The paper feed before a cut, for a Cut block and for the auto-cut.
internal static class CutFeed
{
    // The n of the cut command GS V m n: a feed in motion units, under 1 mm. Every cut sends it.
    // The value is from before options.feedLinesAfterPrint fed lines. A job that does not send the field has these bytes only.
    internal const int MotionUnits = 3;

    private const byte LineFeed = 0x0A;

    // options.feedLinesAfterPrint as LF bytes, like a LineFeed block: LF is the feed that is read from paper on this printer (issue #31),
    // and the n of GS V holds 255 dots at most, under 9 lines. A line has the line spacing of the job; each block sets the text size back to 1 x 1.
    // Over the paper limit it throws PrintContentException.
    public static void AddLines(BlockContext ctx)
    {
        var lines = ctx.Options?.FeedLinesAfterPrint ?? 0;
        if (lines <= 0)
            return;

        ctx.AddPaper(lines * PaperLength.LineDots(ctx.LineSpacing));
        var feed = new byte[lines];
        feed.AsSpan().Fill(LineFeed);
        ctx.Add(feed);
    }
}
