using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing;

// Resolved from the styles and the size field in one place, so the bytes and the paper estimate agree.
internal readonly record struct TextScale(int Width, int Height)
{
    // A size field wins over DoubleWidth and DoubleHeight: one rule, whatever the styles say.
    public static TextScale Of(List<PrintStyle>? styles, TextSize? size)
    {
        if (size is null)
        {
            return new TextScale(
                styles?.Contains(PrintStyle.DoubleWidth) == true ? 2 : 1,
                styles?.Contains(PrintStyle.DoubleHeight) == true ? 2 : 1);
        }

        if (size.Width is < TextSize.Min or > TextSize.Max)
            throw PrintContentException.OutOfRange("size.width", size.Width, TextSize.Min, TextSize.Max);
        if (size.Height is < TextSize.Min or > TextSize.Max)
            throw PrintContentException.OutOfRange("size.height", size.Height, TextSize.Min, TextSize.Max);

        return new TextScale(size.Width, size.Height);
    }

    // GS ! n: the high four bits hold width - 1, the low four bits height - 1.
    public byte[] Command() => [0x1D, 0x21, (byte)(((Width - 1) << 4) | (Height - 1))];
}
