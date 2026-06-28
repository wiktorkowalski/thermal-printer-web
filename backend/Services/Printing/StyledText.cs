using System.Text;
using ESCPOS_NET.Emitters;
using EscPrintStyle = ESCPOS_NET.Emitters.PrintStyle;

namespace ThermalPrinterWeb.Services.Printing;

// Shared by Text and Separator blocks: wraps styled, code-page-encoded text plus
// a trailing LF in reverse / upside-down toggles.
internal static class StyledText
{
    public static List<byte[]> Build(EPSON e, string text, List<Models.PrintStyle>? styles, Encoding encoding)
    {
        List<byte[]> bytes = [];
        var hasReverse = styles?.Contains(Models.PrintStyle.ReverseMode) == true;
        var hasUpsideDown = styles?.Contains(Models.PrintStyle.UpsideDownMode) == true;

        if (hasReverse)
            bytes.Add(e.ReverseMode(true));
        if (hasUpsideDown)
            bytes.Add(e.UpsideDownMode(true));

        bytes.Add(e.SetStyles(MapPrintStyles(styles)));

        var textBytes = encoding.GetBytes(text);
        var newLine = new byte[] { 0x0A }; // LF
        bytes.Add([.. textBytes, .. newLine]);

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
