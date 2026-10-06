using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class TextBlockHandler : IBlockHandler
{
    // One block: 10,000 characters is 209 full lines in Font A; 500 lines = 1.8 m of paper.
    internal const int MaxLength = 10_000;
    internal const int MaxLines = 500;

    public ContentType Type => ContentType.Text;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        var text = item.Content ?? string.Empty;
        if (text.Length > MaxLength)
            throw PrintContentException.OverLimit("text length", text.Length, MaxLength);

        // Only "wrap": true breaks the lines. Without it the text goes to the printer as sent: callers and stored jobs rely on these bytes.
        // The wrap reads at most MaxLength characters, and the limits below count the text after it.
        if (item.Wrap == true)
        {
            text = WordWrap.Wrap(text, PaperLength.Columns(item.Style, item.Size));
            // A break inside a long word adds one character.
            if (text.Length > MaxLength)
                throw PrintContentException.OverLimit("text length", text.Length, MaxLength);
        }

        // Same line breaks as the encoder: it turns CR, FF, NEL, LS and PS into LF.
        var lines = text.ReplaceLineEndings("\n").AsSpan().Count('\n') + 1;
        if (lines > MaxLines)
            throw PrintContentException.OverLimit("text line count", lines, MaxLines);

        ctx.AddRange(StyledText.Build(ctx, text, item.Style, item.Size));
        return Task.CompletedTask;
    }
}
