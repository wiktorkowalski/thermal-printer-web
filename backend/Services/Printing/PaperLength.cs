using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing;

// Estimate of the paper one block feeds, in dots (8 dots = 1 mm). It only has to stop a job
// that empties the roll. The numbers match frontend/src/lib/paper.ts.
internal static class PaperLength
{
    internal const int DotsPerMetre = 8000;

    // Measured line pitch: 11 lines = 40 mm.
    internal const int DefaultLineDots = 29;

    // Font A cell height. DoubleHeight adds one more cell to the line.
    private const int GlyphDots = 24;

    private const int FontAColumns = 48;
    private const int FontBColumns = 64;

    // A QR code has 21 modules per side (version 1) to 177 (version 40).
    private const int QRMinModules = 21;
    private const int QRMaxModules = 177;

    // Modules per data byte at the highest correction level: version 40 holds 1273 bytes in 177 x 177 modules.
    private const int QRModulesPerByte = 25;

    public static int LineDots(int? lineSpacing, List<PrintStyle>? styles = null)
        => Math.Max(lineSpacing ?? DefaultLineDots, GlyphDots)
            + (styles?.Contains(PrintStyle.DoubleHeight) == true ? GlyphDots : 0);

    // The printer wraps a long line; an empty line still feeds one line.
    public static long TextDots(string text, int? lineSpacing, List<PrintStyle>? styles)
    {
        var columns = styles?.Contains(PrintStyle.FontB) == true ? FontBColumns : FontAColumns;
        if (styles?.Contains(PrintStyle.DoubleWidth) == true)
            columns /= 2;

        var lines = 0L;
        // Same line breaks as the encoder: it turns CR, FF, NEL, LS and PS into LF.
        foreach (var line in text.AsSpan().EnumerateLines())
            lines += Math.Max(1, (line.Length + columns - 1) / columns);

        return lines * LineDots(lineSpacing, styles);
    }

    // The printer picks the QR version. This is the largest side the data can need.
    public static int QRCodeDots(int dataBytes, int moduleDots)
    {
        var modules = (int)Math.Ceiling(Math.Sqrt(QRMinModules * QRMinModules + (double)dataBytes * QRModulesPerByte));
        return Math.Clamp(modules, QRMinModules, QRMaxModules) * moduleDots;
    }
}
