using System.Globalization;
using System.Text;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Services.Printing;

// The house style of a plain note (issues #43 and #47). Shared by the HTTP simple-print path
// and the MCP print_note tool so the two render identically.
// The wrap and the date line belong to simple mode only: template mode prints the blocks as sent.
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
    // Three lines: the one that is needed plus two of margin.
    internal const int FeedLines = 3;

    // The date on the strip is the date at the printer, not the UTC date of the container.
    private static readonly TimeZoneInfo StripZone =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Warsaw", out var zone) ? zone : TimeZoneInfo.Utc;

    private static readonly Encoding DefaultEncoding = CodePages.GetEncoding(CodePages.DefaultName);

    public static DateOnly Today(TimeProvider clock)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), StripZone).DateTime);

    public static List<PrintContent> Build(string name, string message, DateOnly date, string? imageBase64 = null)
    {
        List<PrintContent> content =
        [
            Text(Wrap(name, HeaderColumns), PrintStyle.Bold, Alignment.Center),
            Separator(),
            Text(Wrap(message, BodyColumns), PrintStyle.FontB, Alignment.Center),
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

        content.Add(Text(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), PrintStyle.FontB, Alignment.Right));
        content.Add(new() { Type = ContentType.LineFeed, Lines = FeedLines });
        content.Add(new() { Type = ContentType.Cut });

        return content;
    }

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

    // The printer wraps in the middle of a word. This breaks a line at a space; a word longer than
    // the line breaks at the column limit. The line breaks of the caller stay, and a line that fits is not changed.
    // The columns are those of the default code page: a job with another options.codePage can get a line over the limit.
    internal static string Wrap(string text, int columns)
    {
        // The request body can hold 30 MB of text. A text over the limit of a Text block is not read here:
        // the block is rejected for its length, before any work on the text.
        if (text.Length > TextBlockHandler.MaxLength)
            return text;

        var wrapped = new StringBuilder(text.Length + text.Length / columns);
        var first = true;
        // The same line breaks as the encoder: CR, CRLF, FF, NEL, LS and PS count as LF.
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (!first)
                wrapped.Append('\n');
            first = false;
            AppendWrapped(wrapped, line, columns);
        }

        return wrapped.ToString();
    }

    private static void AppendWrapped(StringBuilder wrapped, ReadOnlySpan<char> line, int columns)
    {
        var width = 0;
        var at = 0;
        while (at < line.Length)
        {
            var wordStart = at;
            while (wordStart < line.Length && IsSpace(line[wordStart]))
                wordStart++;
            var wordEnd = wordStart;
            while (wordEnd < line.Length && !IsSpace(line[wordEnd]))
                wordEnd++;

            // One column each: a tab prints as a space.
            var gap = line[at..wordStart];
            var word = line[wordStart..wordEnd];
            at = wordEnd;

            // Spaces at the end of a line that do not fit.
            if (word.IsEmpty && width + gap.Length > columns)
                return;

            // The spaces at a break are not printed. Spaces at the start of a line go too when the word does not fit after them.
            var wordColumns = ColumnsOf(word);
            if (width + gap.Length + wordColumns > columns)
            {
                if (width > 0)
                    wrapped.Append('\n');
                width = 0;
                gap = [];
            }

            wrapped.Append(gap);
            width += gap.Length;
            if (width + wordColumns <= columns)
            {
                wrapped.Append(word);
                width += wordColumns;
                continue;
            }

            // A word longer than the line. The characters are copied as they came: a lone surrogate stays.
            var copied = 0;
            foreach (var rune in word.EnumerateRunes())
            {
                var runeColumns = ColumnsOf(rune);
                if (width > 0 && width + runeColumns > columns)
                {
                    wrapped.Append('\n');
                    width = 0;
                }

                wrapped.Append(word.Slice(copied, rune.Utf16SequenceLength));
                copied += rune.Utf16SequenceLength;
                width += runeColumns;
            }
        }
    }

    private static bool IsSpace(char character) => character is ' ' or '\t';

    private static int ColumnsOf(ReadOnlySpan<char> word)
    {
        var columns = 0;
        foreach (var rune in word.EnumerateRunes())
            columns += ColumnsOf(rune);
        return columns;
    }

    // One byte at the printer is one column. Most characters are one byte; an arrow prints as "->", an emoji as "??".
    private static int ColumnsOf(Rune rune)
        => rune.IsAscii ? 1 : PrinterSafeText.Encode(rune.ToString(), DefaultEncoding, out _).Length;
}
