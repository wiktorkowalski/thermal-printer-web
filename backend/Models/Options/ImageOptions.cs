namespace ThermalPrinterWeb.Models;

public class ImageOptions
{
    // 576 = full print-head width of the V330M (80mm head); was 500, ~13% short.
    public int? MaxWidth { get; set; } = 576;
    public int? MaxHeight { get; set; } = 576;
    public bool PreserveAspectRatio { get; set; } = true;
    public bool UseLegacyMode { get; set; } = true;

    // Maps to ESCPOS PrintImage isHiDPI; true preserves prior behavior.
    public bool HighDensity { get; set; } = true;
}
