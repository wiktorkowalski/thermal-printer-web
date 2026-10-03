using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing;

// An estimate, in dots (8 dots = 1 mm): it only has to stop a job that empties the roll,
// so it takes the larger value where the printer decides.
internal static class PaperLength
{
    internal const int DotsPerMetre = 8000;

    // One job: 32,000 dots = 4 m. The longest Text block (500 DoubleHeight lines, 3.3 m)
    // and 20 images of the default height (1.4 m) pass; a receipt is under 1 m.
    internal const int MaxDots = 32_000;

    // Measured line pitch: 11 lines = 40 mm. Same value as frontend/src/lib/paper.ts.
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
    public static int TextDots(string text, int? lineSpacing, List<PrintStyle>? styles)
    {
        var columns = styles?.Contains(PrintStyle.FontB) == true ? FontBColumns : FontAColumns;
        if (styles?.Contains(PrintStyle.DoubleWidth) == true)
            columns /= 2;

        var lines = 0;
        // Same line breaks as the encoder: it turns CR, FF, NEL, LS and PS into LF.
        foreach (var line in text.AsSpan().EnumerateLines())
            lines += Math.Max(1, (line.Length + columns - 1) / columns);

        return lines * LineDots(lineSpacing, styles);
    }

    // The size the printer gives an image: it only scales down, to fit inside the limits.
    public static int ImageDots(int width, int height, int maxWidth, int maxHeight, bool preserveAspectRatio)
    {
        if (width <= maxWidth && height <= maxHeight)
            return height;
        if (!preserveAspectRatio)
            return maxHeight;

        var scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        return Math.Max(1, (int)Math.Round(height * scale));
    }

    // The printer picks the QR version. This is the largest side the data can need.
    public static int QRCodeDots(int dataBytes, int moduleDots)
    {
        var modules = (int)Math.Ceiling(Math.Sqrt(QRMinModules * QRMinModules + (double)dataBytes * QRModulesPerByte));
        return Math.Min(modules, QRMaxModules) * moduleDots;
    }
}
