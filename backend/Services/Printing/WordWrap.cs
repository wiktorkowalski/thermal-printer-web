using System.Text;
using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Services.Printing;

// The one word wrap of the server: simple mode (SimpleNote) and a Text block with "wrap": true (issue #55).
internal static class WordWrap
{
    // "-", "->", "(": a word of this length at most can be kept with the word before it.
    private const int MaxPunctuationLength = 2;

    private static readonly Encoding DefaultEncoding = CodePages.GetEncoding(CodePages.DefaultName);

    // The printer wraps in the middle of a word. This breaks a line at a space; a word longer than
    // the line breaks at the column limit. The line breaks of the caller stay, and a line that fits is not changed.
    // The columns are those of the default code page: a job with another code page can get a line over the limit.
    // keepPunctuation (text mode, issue #53): a word of 1 or 2 punctuation characters does not start a line that the wrap makes;
    // the word before it goes to the new line with it. Off for simple mode and "wrap": true: their bytes do not change.
    internal static string Wrap(string text, int columns, bool keepPunctuation = false)
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
            AppendWrapped(wrapped, line, columns, keepPunctuation);
        }

        return wrapped.ToString();
    }

    private static void AppendWrapped(StringBuilder wrapped, ReadOnlySpan<char> line, int columns, bool keepPunctuation)
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
            if (width + gap.Length + wordColumns > columns
                || (keepPunctuation && width > 0 && PunctuationWouldStartALine(line[at..], width + gap.Length + wordColumns, wordColumns, columns)))
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

    // True when the word after this one is 1 or 2 punctuation characters that do not fit in the line and do fit after this word in a new line.
    // It reads at most the spaces and 3 characters of the rest.
    private static bool PunctuationWouldStartALine(ReadOnlySpan<char> rest, int widthWithWord, int wordColumns, int columns)
    {
        var start = 0;
        while (start < rest.Length && IsSpace(rest[start]))
            start++;
        var end = start;
        while (end < rest.Length && end - start <= MaxPunctuationLength && !IsSpace(rest[end]))
            end++;

        var next = rest[start..end];
        if (next.IsEmpty || next.Length > MaxPunctuationLength)
            return false;
        foreach (var character in next)
        {
            if (!char.IsPunctuation(character) && !char.IsSymbol(character))
                return false;
        }

        var tail = start + ColumnsOf(next);
        return widthWithWord + tail > columns && wordColumns + tail <= columns;
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
