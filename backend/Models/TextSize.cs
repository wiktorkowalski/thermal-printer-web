using System.ComponentModel;

namespace ThermalPrinterWeb.Models;

// GS ! n takes 1 to 8 for each axis. The [Description] texts repeat Min and Max.
public sealed class TextSize
{
    internal const int Min = 1;
    internal const int Max = 8;

    [Description("Width multiplier, a whole number from 1 to 8. Default 1. One line holds 48 / width characters, rounded down: 48, 24, 16, 12, 9, 8, 6, 6 (with FontB 64 / width: 64, 32, 21, 16, 12, 10, 9, 8).")]
    public int Width { get; set; } = Min;
    [Description("Height multiplier, a whole number from 1 to 8. Default 1. It does not change the characters per line; each step adds 3 mm to the line.")]
    public int Height { get; set; } = Min;
}
