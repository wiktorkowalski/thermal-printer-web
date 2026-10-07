using System.Globalization;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing;

// The house style of a plain note (issues #43 and #47). Shared by the HTTP simple-print path
// and the MCP print_note tool so the two render identically.
// Simple mode always wraps (WordWrap) and adds the date line. Template mode prints the blocks as sent;
// a Text block wraps only with "wrap": true.
internal static class SimpleNote
{
    // Every text line prints at 2 x 3.
    internal const int Width = 2;
    internal const int Height = 3;

    // Font A, bold: taller than the body (72 dots against 51) and 24 characters per line.
    internal static readonly int HeaderColumns = PaperLength.Columns(fontB: false, Width);

    // Font B (owner decision, issue #47): 32 characters per line.
    internal static readonly int BodyColumns = PaperLength.Columns(fontB: true, Width);

    // Font A at 1 x 1: 48 characters fill the 576 dots of the head, like 32 body characters.
    internal static readonly int SeparatorLength = PaperLength.Columns(fontB: false, widthMultiplier: 1);

    // The cut command alone cuts through the last text line: it is about one line short (issue #31, row F1).
    // The note ends with the default gap as a LineFeed block, so its Cut block adds no line (CutFeed).
    internal const int FeedLines = CutFeed.DefaultLines;

    internal const string DateFormat = "yyyy-MM-dd";

    // The date on the strip is the date at the printer, not the UTC date of the container.
    private static readonly TimeZoneInfo StripZone =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Warsaw", out var zone) ? zone : TimeZoneInfo.Utc;

    public static DateOnly Today(TimeProvider clock)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), StripZone).DateTime);

    public static List<PrintContent> Build(string name, string message, DateOnly date, string? imageBase64 = null)
    {
        List<PrintContent> content =
        [
            Text(WordWrap.Wrap(name, HeaderColumns), PrintStyle.Bold, Alignment.Center),
            Separator(),
            Text(WordWrap.Wrap(message, BodyColumns), PrintStyle.FontB, Alignment.Center),
            Separator()
        ];

        if (!string.IsNullOrEmpty(imageBase64))
        {
            content.Add(new PrintContent
            {
                Type = ContentType.Image,
                Content = imageBase64
            });
            content.Add(Separator());
        }

        content.Add(DateLine(DateText(date)));
        content.Add(new() { Type = ContentType.LineFeed, Lines = FeedLines });
        content.Add(new() { Type = ContentType.Cut });

        return content;
    }

    // The date as it prints. The signature line of template mode starts with it (SignatureLine).
    internal static string DateText(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    // The last text line of a strip in the house style: the date line here, the signature line in template mode.
    internal static PrintContent DateLine(string text) => Text(text, PrintStyle.FontB, Alignment.Right);

    private static PrintContent Separator()
        => new() { Type = ContentType.Separator, SeparatorChar = "=", SeparatorLength = SeparatorLength };

    private static PrintContent Text(string text, PrintStyle style, Alignment alignment) => new()
    {
        Type = ContentType.Text,
        Content = text,
        Alignment = alignment,
        Style = [style],
        Size = new TextSize { Width = Width, Height = Height }
    };
}
