using System.Globalization;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing;

// How the print ended, and the blocks for the journal row. A job that printed, or that the printer refused: the blocks with the signature line.
// A payload fault at a block of the caller: the blocks as sent, so the block number of the answer is the number in the row.
// A fault of options.sign: no blocks, so the row cannot be reprinted. Its reprint would print the document with no line.
public sealed record SignedPrint(PrintResult Result, List<PrintContent>? Content);

// The signature line of template mode (issue #54): options.sign makes one last text line "yyyy-MM-dd * name".
// The entry points of a new print call it (POST /api/printer with content, the MCP print tool), in place of IPrinterService.PrintAsync.
// The line is a Text block like the date line of simple mode, so the journal stores it with its date and a reprint prints that date:
// the reprint sends the stored blocks to PrinterService, which does not read options.sign.
// Public on purpose: the controller and the MCP tool take it as an injected parameter.
public sealed class SignatureLine(IPrinterService printer, TimeProvider clock, ILogger<SignatureLine> logger)
{
    internal const string Field = "options.sign";

    // Between the date and the name.
    internal const string Mark = " * ";

    // The line is one body line of the house style: a longer one wraps in the middle of the name.
    internal static readonly int MaxSignColumns = SimpleNote.BodyColumns - SimpleNote.DateFormat.Length - Mark.Length;

    internal const string NotOneLineReason = Field + ": must be one line with no control character";

    internal static readonly string NoBlockLeftReason =
        $"{Field}: the signature line is one block, and the document holds {PrinterService.MaxBlocks} blocks already";

    private const string BlockPrefix = "Block ";

    // U+2028 and U+2029: line breaks for the encoder, and no control characters for char.IsControl.
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    public async Task<SignedPrint> PrintAsync(List<PrintContent> content, PrintOptions? options)
    {
        // Not asked: the job of before the field, byte for byte. Over the block limit: the print path rejects the document as it came.
        if (options?.Sign is not { } sign || string.IsNullOrWhiteSpace(sign) || content.Count > PrinterService.MaxBlocks)
            return new SignedPrint(await printer.PrintAsync(content, options), content);

        if (Fault(sign, content.Count) is { } reason)
            return new SignedPrint(PrintResult.Invalid(reason), null);

        var at = InsertIndex(content);
        List<PrintContent> signed = [.. content[..at], Build(sign, SimpleNote.Today(clock)), .. content[at..]];
        var result = WithCallerBlockNumbers(await printer.PrintAsync(signed, options), at);
        if (result.Failure != PrintFailure.Validation)
            return new SignedPrint(result, signed);

        var atTheLine = result.Error?.StartsWith(Field, StringComparison.Ordinal) == true;
        return new SignedPrint(result, atTheLine ? null : content);
    }

    internal static PrintContent Build(string sign, DateOnly date)
        => SimpleNote.DateLine(SimpleNote.DateText(date) + Mark + sign);

    // After the last block that can print. The LineFeed and Cut blocks at the end stay the end: the gap and the cut come after the line.
    // A Signal and a CodePage block move no paper (CutFeed), so they stay behind the line too.
    internal static int InsertIndex(List<PrintContent> content)
    {
        var at = content.Count;
        while (at > 0 && content[at - 1] is { Type: ContentType.LineFeed or ContentType.Cut or ContentType.Signal or ContentType.CodePage })
            at--;
        return at;
    }

    // Numbers only: the name is caller text and goes to no log and no answer.
    private string? Fault(string sign, int blockCount)
    {
        // First: the request body can hold 30 MB of text, and the two checks below read every character.
        // A name of over twice the limit is over it whatever it holds: a character is at most two UTF-16 units and at least one column.
        if (sign.Length > MaxSignColumns * 2)
            return TooLong(sign.Length);

        // The line breaks of the encoder (PrinterSafeText) and every other control character.
        if (sign.AsSpan().ContainsAny(LineSeparator, ParagraphSeparator) || sign.Any(char.IsControl))
        {
            logger.LogWarning("Rejected print: {Field} holds a control character or a line break", Field);
            return NotOneLineReason;
        }

        // A character that prints as two counts as two (WordWrap), in the default code page.
        var columns = WordWrap.ColumnsOf(sign);
        if (columns > MaxSignColumns)
            return TooLong(columns);

        if (blockCount == PrinterService.MaxBlocks)
        {
            logger.LogWarning("Rejected print: {Field} needs one block, the document has {BlockCount} of {MaxBlocks}", Field, blockCount, PrinterService.MaxBlocks);
            return NoBlockLeftReason;
        }

        return null;
    }

    private string TooLong(int length)
    {
        logger.LogWarning("Rejected print: {Field} has the length {Length}, the limit is {MaxLength}", Field, length, MaxSignColumns);
        return PrintContentException.OverLimit($"{Field} length", length, MaxSignColumns).Message;
    }

    // The print path numbers the blocks with the signature line among them. The caller did not send that block:
    // a fault at it names the field, and a block after it gets the number that it has in the request.
    internal static PrintResult WithCallerBlockNumbers(PrintResult result, int signatureIndex)
    {
        if (result is not { Failure: PrintFailure.Validation, Error: { } error } || !error.StartsWith(BlockPrefix, StringComparison.Ordinal))
            return result;

        var digits = error.AsSpan(BlockPrefix.Length);
        var end = digits.IndexOfAnyExceptInRange('0', '9');
        if (end <= 0 || !int.TryParse(digits[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < signatureIndex)
            return result;

        var rest = digits[end..];
        if (index > signatureIndex)
            return PrintResult.Invalid($"{BlockPrefix}{index - 1}{rest}");

        // "Block 4 (Text): reason" or "Block 4: reason".
        var reasonAt = rest.IndexOf(": ", StringComparison.Ordinal);
        return reasonAt < 0 ? result : PrintResult.Invalid($"{Field}{rest[reasonAt..]}");
    }
}
