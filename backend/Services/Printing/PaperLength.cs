using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing;

// An estimate, in dots (8 dots = 1 mm): it only has to stop a job that empties the roll,
// so it takes the larger value where the printer decides.
internal static class PaperLength
{
    internal const int DotsPerMetre = 8000;

    // One job: 32,000 dots = 4 m. The longest Text block at 2x (500 DoubleHeight lines, 3.3 m)
    // and 20 images of the default height (1.4 m) pass; a receipt is under 1 m.
    // Taller text reaches the limit sooner: 162 lines at height 8.
    internal const int MaxDots = 32_000;

    // Measured line pitch: 11 lines = 40 mm. Same value as frontend/src/lib/paper.ts.
    internal const int DefaultLineDots = 29;

    // Font A cell height.
    private const int GlyphDots = 24;

    private const byte LineFeed = 0x0A;

    // Measured: the paper between the head and the cutter. Each cut feeds it.
    private const int CutterOffsetDots = 124;

    private const int FontAColumns = 48;
    private const int FontBColumns = 64;

    // A QR code has 21 modules per side (version 1) to 177 (version 40).
    private const int QRMinModules = 21;
    private const int QRMaxModules = 177;

    // Modules per data byte at the highest correction level: version 40 holds 1273 bytes in 177 x 177 modules.
    private const int QRModulesPerByte = 25;

    // Each step of the height multiplier adds one more cell to the line.
    public static int LineDots(int? lineSpacing, int heightMultiplier = 1)
        => Math.Max(lineSpacing ?? DefaultLineDots, GlyphDots) + (heightMultiplier - 1) * GlyphDots;

    // The head is 576 dots; a Font A cell is 12 dots wide, a Font B cell 9. The division rounds down
    // like the printer: at width 5 a Font A line holds 9 characters (540 dots), not 9.6.
    public static int Columns(bool fontB, int widthMultiplier)
        => (fontB ? FontBColumns : FontAColumns) / widthMultiplier;

    // The characters per line of a Text block. It rejects a size outside the range.
    public static int Columns(List<PrintStyle>? styles, TextSize? size)
        => Columns(styles?.Contains(PrintStyle.FontB) == true, TextScale.Of(styles, size).Width);

    // The printer wraps a long line; an empty line still feeds one line.
    // Counted on the encoded text: one byte is one column, and one character can be three bytes.
    public static int TextDots(ReadOnlySpan<byte> encoded, int? lineSpacing, bool fontB, TextScale scale)
    {
        var columns = Columns(fontB, scale.Width);

        var lines = 0;
        foreach (var line in encoded.Split(LineFeed))
            lines += Math.Max(1, (line.End.GetOffset(encoded.Length) - line.Start.GetOffset(encoded.Length) + columns - 1) / columns);

        return lines * LineDots(lineSpacing, scale.Height);
    }

    // The cut command alone: GS V m n feeds n motion units, and one unit is at most one dot.
    // The lines of options.feedLinesAfterPrint are counted apart (CutFeed). The parameter stays: SimpleNoteTests calls it with its own 3.
    public static int CutDots(int motionUnits) => motionUnits + CutterOffsetDots;

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
