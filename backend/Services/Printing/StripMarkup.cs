using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Services.Printing;

// Text mode (issue #53): plain text with markers at the start of a line, compiled to the blocks of the house style (SimpleNote).
// A pure step: text in, blocks out. The blocks go through PrinterService.PrintAsync like every job: same limits, same handlers.
// One line of the text is one block, so "Block N" in an error is line N + 1 of the text.
// A line that fits no marker is body text: no line is a syntax error.
internal static class StripMarkup
{
    // The limit of one Text block. A longer text is not compiled.
    internal const int MaxLength = TextBlockHandler.MaxLength;

    // A rule is a line of 3 or more of one of these characters.
    internal const int MinRuleLength = 3;

    // "999. " is the longest number of a list item.
    internal const int MaxNumberDigits = 3;

    // A longer indent or marker gets no hanging indent: half of the line stays for the text.
    internal static readonly int MaxHangColumns = SimpleNote.BodyColumns / 2;

    internal const string ConflictError = "text cannot go with content, name, message or imageBase64: send text alone";

    // "align must be Left, Center or Right": the same problem text over HTTP and in the MCP answer.
    internal static readonly string AlignProblem = $"must be {BlockEnums.Names<Alignment>()}";
    internal static readonly string AlignError = $"align {AlignProblem}";

    // A space or a tab: the one meaning of "space" in the markup. Another Unicode space is text.
    private static ReadOnlySpan<char> Blanks => [' ', '\t'];

    private const string HeaderMarker = "# ";
    private const string BoldMarker = "## ";
    private const string BoldFence = "**";
    private const string ReverseFence = "==";
    private const string QRCodeMarker = "qr: ";
    private const char Escape = '\\';

    // A name of Alignment, letter case aside. Not the enum binder: it takes a number with no name. Null or empty is Center.
    public static bool TryParseAlign(string? align, out Alignment alignment)
    {
        alignment = Alignment.Center;
        if (string.IsNullOrEmpty(align))
            return true;

        foreach (var value in Enum.GetValues<Alignment>())
        {
            if (string.Equals(align, value.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                alignment = value;
                return true;
            }
        }

        return false;
    }

    // One pass over the text. Each line is read a fixed number of times, and the wrap is the one of simple mode.
    public static List<PrintContent> Compile(string text, Alignment align = Alignment.Center)
    {
        // The request body can hold 30 MB of text. A text over the limit goes to one Text block as it is:
        // the block is rejected for its length, before any work on the text (the way of WordWrap).
        if (text.Length > MaxLength)
            return [SimpleNote.Text(text, align, PrintStyle.FontB)];

        var blocks = new List<PrintContent>();
        var pendingEmptyLine = false;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            // One block over the limit (two, with an empty line before it) is enough for the print path to reject the job. A text of 10,000 line breaks
            // must not make 10,000 blocks: the journal row of a rejected job holds its blocks.
            if (blocks.Count > PrinterService.MaxBlocks)
                break;

            // The empty line after the last line break of the text is not a line.
            if (pendingEmptyLine)
                blocks.Add(EmptyLine());

            var line = raw.TrimEnd(Blanks);
            pendingEmptyLine = line.IsEmpty;
            if (!pendingEmptyLine)
                blocks.Add(CompileLine(line, align));
        }

        return blocks;
    }

    private static PrintContent EmptyLine() => new() { Type = ContentType.LineFeed, Lines = 1 };

    // The line is not empty and does not end with a space.
    private static PrintContent CompileLine(ReadOnlySpan<char> line, Alignment align)
    {
        if (line[0] == Escape)
            return line.Length == 1 ? EmptyLine() : Body(line[1..], align);

        if (line.StartsWith(HeaderMarker))
            return SimpleNote.Text(Wrap(line[HeaderMarker.Length..].TrimStart(Blanks), SimpleNote.HeaderColumns), Alignment.Center, PrintStyle.Bold);

        if (line.StartsWith(BoldMarker))
            return Body(line[BoldMarker.Length..].TrimStart(Blanks), align, PrintStyle.Bold);

        if (IsRule(line, '='))
            return SimpleNote.Separator("=");
        if (IsRule(line, '-'))
            return SimpleNote.Separator("-");

        if (Fenced(line, BoldFence, out var bold))
            return Body(bold, align, PrintStyle.Bold);
        if (Fenced(line, ReverseFence, out var reverse))
            return Body(reverse, align, PrintStyle.ReverseMode);

        if (line.StartsWith(QRCodeMarker, StringComparison.OrdinalIgnoreCase))
            return new() { Type = ContentType.QRCode, Content = line[QRCodeMarker.Length..].TrimStart(Blanks).ToString() };

        var hang = HangColumns(line);
        return hang > 0 ? Hanging(line, hang) : Body(line, align);
    }

    private static PrintContent Body(ReadOnlySpan<char> text, Alignment align, params PrintStyle[] styles)
        => SimpleNote.Text(Wrap(text, SimpleNote.BodyColumns), align, [PrintStyle.FontB, .. styles]);

    private static string Wrap(ReadOnlySpan<char> text, int columns)
        => WordWrap.Wrap(text.ToString(), columns, keepPunctuation: true);

    private static bool IsRule(ReadOnlySpan<char> line, char character)
        => line.Length >= MinRuleLength && !line.ContainsAnyExcept(character);

    // "**text**" or "==text==": the whole line, with text between the two fences.
    // Not "**a** and **b**" and not "=== title ===": a line with a fence inside, or with one more fence character at an end, is body text.
    private static bool Fenced(ReadOnlySpan<char> line, string fence, out ReadOnlySpan<char> inner)
    {
        inner = default;
        if (line.Length <= 2 * fence.Length || !line.StartsWith(fence) || !line.EndsWith(fence))
            return false;

        inner = line[fence.Length..^fence.Length];
        if (inner[0] == fence[0] || inner[^1] == fence[0] || inner.Contains(fence, StringComparison.Ordinal))
            return false;

        inner = inner.Trim(Blanks);
        return !inner.IsEmpty;
    }

    // The columns of the indent and the list marker of a line: the lines that the wrap adds start there. 0: the line has neither.
    // Markers: "- ", "* ", "1. " or "1) " (1 to 3 digits), then "[ ] " or "[x] "; a check box can stand alone.
    private static int HangColumns(ReadOnlySpan<char> line)
    {
        var at = 0;
        while (at < line.Length && line[at] is ' ' or '\t')
            at++;

        at += BulletLength(line[at..]);
        at += CheckBoxLength(line[at..]);
        return at;
    }

    // The line does not end with a space, so text comes after a marker that ends with one.
    private static int BulletLength(ReadOnlySpan<char> text)
    {
        if (text.Length > 1 && text[0] is '-' or '*' && text[1] == ' ')
            return 2;

        var digits = 0;
        while (digits < text.Length && digits <= MaxNumberDigits && char.IsAsciiDigit(text[digits]))
            digits++;
        return digits is > 0 and <= MaxNumberDigits && text.Length > digits + 1 && text[digits] is '.' or ')' && text[digits + 1] == ' '
            ? digits + 2
            : 0;
    }

    private static int CheckBoxLength(ReadOnlySpan<char> text)
        => text.Length > 3 && text[0] == '[' && text[1] is ' ' or 'x' or 'X' && text[2] == ']' && text[3] == ' ' ? 4 : 0;

    // A list item or an indented line: Left, and each line that the wrap adds starts under the text of the first one.
    private static PrintContent Hanging(ReadOnlySpan<char> line, int hang)
    {
        // A tab prints as a space.
        var prefix = line[..hang].ToString().Replace('\t', ' ');
        var text = line[hang..].TrimStart(Blanks);
        if (hang > MaxHangColumns)
            return SimpleNote.Text(Wrap(string.Concat(prefix, text), SimpleNote.BodyColumns), Alignment.Left, PrintStyle.FontB);

        var wrapped = Wrap(text, SimpleNote.BodyColumns - hang);
        return SimpleNote.Text(prefix + wrapped.Replace("\n", "\n" + new string(' ', hang)), Alignment.Left, PrintStyle.FontB);
    }
}
