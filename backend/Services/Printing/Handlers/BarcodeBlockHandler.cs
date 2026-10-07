using ThermalPrinterWeb.Models;
using EPSON = ESCPOS_NET.Emitters.EPSON;
using EscBarcodeType = ESCPOS_NET.Emitters.BarcodeType;
using EscBarWidth = ESCPOS_NET.Emitters.BarWidth;
using EscBarLabelPosition = ESCPOS_NET.Emitters.BarLabelPrintPosition;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class BarcodeBlockHandler : IBlockHandler
{
    // GS k m n: the fourth byte is the payload length.
    private const int HeaderLength = 4;
    private const int LengthIndex = 3;

    // GS h n takes one byte; 0 is not a height.
    internal const int MinHeightInDots = 1;
    internal const int MaxHeightInDots = 255;

    public ContentType Type => ContentType.Barcode;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        var opts = item.BarcodeOptions ?? new BarcodeOptions();
        if (opts.HeightInDots is < MinHeightInDots or > MaxHeightInDots)
            throw PrintContentException.OutOfRange("barcodeOptions.heightInDots", opts.HeightInDots.Value, MinHeightInDots, MaxHeightInDots);

        if (string.IsNullOrEmpty(item.Content))
            return Task.CompletedTask;

        var e = ctx.Emitter;

        // First: a rejected barcode must not leave its settings in the document.
        var command = BuildCommand(e, item.Content, opts.Type);

        // After BuildCommand: the content is valid for the type, so the width counts what goes out.
        // The numbers are the only caller content in the text.
        var moduleDots = BarcodeWidth.ModuleDots(opts.Width);
        var countedDots = moduleDots ?? ctx.BarModuleDots ?? BarcodeWidth.UnsetDots;
        if (BarcodeWidth.Dots(opts.Type, item.Content, countedDots) is { } dots && dots > BarcodeWidth.MaxDots)
        {
            // A stored job from before the width rule printed without this barcode: its reprint does the same.
            // The block sends no setting, no bars and no caption and counts no paper. Every other check of the block ran before this line.
            if (ctx.IsReprint)
            {
                ctx.SkippedBarcodes++;
                return Task.CompletedTask;
            }

            // Thin is the narrowest width that the API has. It also helps a block with no width that got a wider module from the block before it.
            var fix = countedDots > BarcodeWidth.ThinDots
                ? "use barcodeOptions.width Thin or shorter content"
                : "use shorter content";
            throw new PrintContentException(
                $"the barcode is at least {dots} dots wide and the paper holds {BarcodeWidth.MaxDots}; the printer drops a wider barcode: {fix}");
        }

        // The bars plus the caption lines. A null height keeps what the printer has: count the tallest.
        var captionLines = opts.LabelPosition == BarLabelPosition.Both ? 2 : 1;
        ctx.AddPaper((opts.HeightInDots ?? MaxHeightInDots) + captionLines * PaperLength.LineDots(ctx.LineSpacing));

        if (opts.HeightInDots.HasValue)
            ctx.Add(e.SetBarcodeHeightInDots(opts.HeightInDots.Value));
        if (opts.Width.HasValue)
        {
            ctx.Add(e.SetBarWidth(MapBarWidth(opts.Width.Value)));
            ctx.BarModuleDots = moduleDots;
        }
        if (opts.LabelPosition.HasValue)
            ctx.Add(e.SetBarLabelPosition(MapBarLabelPosition(opts.LabelPosition.Value)));
        if (opts.UseFontB.HasValue)
            ctx.Add(e.SetBarLabelFontB(opts.UseFontB.Value));

        ctx.Add(command);
        return Task.CompletedTask;
    }

    private static byte[] BuildCommand(EPSON emitter, string content, Models.BarcodeType type)
    {
        // No symbology here encodes more, and ESCPOS_NET lets control characters
        // through for CODE128 and GS1-128. UTF-8 bytes of other characters have no bars.
        if (content.AsSpan().ContainsAnyExceptInRange(' ', '~'))
            throw new PrintContentException($"a {type} barcode holds printable ASCII only");

        byte[] command;
        try
        {
            command = emitter.PrintBarcode(MapBarcodeType(type), content);
        }
        catch (ArgumentException)
        {
            // ESCPOS_NET checks length and character set per symbology. Its message
            // repeats the content, so name the symbology only.
            throw new PrintContentException($"content is not a valid {type} barcode");
        }

        // ESCPOS_NET grows CODE128 content (code set prefix, doubled '{') after its
        // length check; past 255 bytes the length byte wraps and the rest prints as text.
        if (command.Length - HeaderLength != command[LengthIndex])
            throw new PrintContentException($"content is too long for a {type} barcode");

        return command;
    }

    private static EscBarcodeType MapBarcodeType(Models.BarcodeType type) => type switch
    {
        Models.BarcodeType.UPC_A => EscBarcodeType.UPC_A,
        Models.BarcodeType.UPC_E => EscBarcodeType.UPC_E,
        Models.BarcodeType.EAN13 => EscBarcodeType.JAN13_EAN13,
        Models.BarcodeType.EAN8 => EscBarcodeType.JAN8_EAN8,
        Models.BarcodeType.CODE39 => EscBarcodeType.CODE39,
        Models.BarcodeType.ITF => EscBarcodeType.ITF,
        Models.BarcodeType.CODABAR => EscBarcodeType.CODABAR_NW_7,
        Models.BarcodeType.CODE128 => EscBarcodeType.CODE128,
        Models.BarcodeType.GS1_128 => EscBarcodeType.GS1_128,
        Models.BarcodeType.GS1_DATABAR_OMNIDIRECTIONAL => EscBarcodeType.GS1_DATABAR_OMNIDIRECTIONAL,
        _ => EscBarcodeType.CODE128
    };

    private static EscBarWidth MapBarWidth(Models.BarWidth width) => width switch
    {
        Models.BarWidth.Thin => EscBarWidth.Thin,
        Models.BarWidth.Thick => EscBarWidth.Thick,
        _ => EscBarWidth.Default
    };

    private static EscBarLabelPosition MapBarLabelPosition(Models.BarLabelPosition pos) => pos switch
    {
        Models.BarLabelPosition.None => EscBarLabelPosition.None,
        Models.BarLabelPosition.Above => EscBarLabelPosition.Above,
        Models.BarLabelPosition.Below => EscBarLabelPosition.Below,
        Models.BarLabelPosition.Both => EscBarLabelPosition.Both,
        _ => EscBarLabelPosition.Below
    };
}
