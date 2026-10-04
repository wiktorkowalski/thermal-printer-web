using ESCPOS_NET.Emitters;
using EscPrintStyle = ESCPOS_NET.Emitters.PrintStyle;

namespace ThermalPrinterWeb.Services.Printing;

// Shared by Text and Separator blocks: wraps styled, code-page-encoded text plus
// a trailing LF in reverse / upside-down toggles.
internal static class StyledText
{
    private const EscPrintStyle DoubleSize = EscPrintStyle.DoubleWidth | EscPrintStyle.DoubleHeight;

    private static readonly TextScale NormalScale = new(1, 1);

    public static List<byte[]> Build(BlockContext ctx, string text, List<Models.PrintStyle>? styles, Models.TextSize? size)
    {
        var e = ctx.Emitter;
        // First: it rejects a size outside the range.
        var scale = TextScale.Of(styles, size);
        var encoded = ctx.EncodeText(text);
        var fontB = styles?.Contains(Models.PrintStyle.FontB) == true;
        ctx.AddPaper(PaperLength.TextDots(encoded, ctx.LineSpacing, fontB, scale));
        List<byte[]> bytes = [];
        var hasReverse = styles?.Contains(Models.PrintStyle.ReverseMode) == true;
        var hasUpsideDown = styles?.Contains(Models.PrintStyle.UpsideDownMode) == true;

        if (hasReverse)
            bytes.Add(e.ReverseMode(true));
        if (hasUpsideDown)
            bytes.Add(e.UpsideDownMode(true));

        // No size field: ESC ! n alone, with its two size bits. A block of an older caller keeps its bytes.
        // With a size field: ESC ! n without the two size bits, then GS ! n. ESC ! sets the size too,
        // so it must come first.
        var sized = size is not null;
        var escStyles = MapPrintStyles(styles);
        bytes.Add(e.SetStyles(sized ? escStyles & ~DoubleSize : escStyles));
        if (sized)
            bytes.Add(scale.Command());

        bytes.Add([.. encoded, 0x0A]); // trailing LF

        // The next block starts at 1 x 1: GS ! 0, and ESC ! 0 clears the size bits as well.
        if (sized)
            bytes.Add(NormalScale.Command());
        bytes.Add(e.SetStyles(EscPrintStyle.None));

        if (hasUpsideDown)
            bytes.Add(e.UpsideDownMode(false));
        if (hasReverse)
            bytes.Add(e.ReverseMode(false));

        return bytes;
    }

    private static EscPrintStyle MapPrintStyles(List<Models.PrintStyle>? styles)
    {
        if (styles == null || styles.Count == 0)
            return EscPrintStyle.None;

        var result = EscPrintStyle.None;
        foreach (var style in styles)
        {
            result |= style switch
            {
                Models.PrintStyle.Bold => EscPrintStyle.Bold,
                Models.PrintStyle.Italic => EscPrintStyle.Italic,
                Models.PrintStyle.Underline => EscPrintStyle.Underline,
                Models.PrintStyle.DoubleHeight => EscPrintStyle.DoubleHeight,
                Models.PrintStyle.DoubleWidth => EscPrintStyle.DoubleWidth,
                Models.PrintStyle.FontB => EscPrintStyle.FontB,
                _ => EscPrintStyle.None
            };
        }
        return result;
    }
}
