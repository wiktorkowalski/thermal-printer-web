using System.ComponentModel;

namespace ThermalPrinterWeb.Models;

public class ImageOptions
{
    // 576 = full print-head width of the V330M (80mm head); was 500, ~13% short.
    [Description("Largest width in dots, 1 to 576. 576 (default) is the full paper width.")]
    public int? MaxWidth { get; set; } = 576;
    [Description("Largest height in dots, 1 to 4096. Default 576.")]
    public int? MaxHeight { get; set; } = 576;
    [Description("Keep the aspect ratio when the image is scaled down. Default true.")]
    public bool PreserveAspectRatio { get; set; } = true;
    [Description("Use the older raster image command. Default true.")]
    public bool UseLegacyMode { get; set; } = true;

    // Maps to ESCPOS PrintImage isHiDPI. In legacy mode the bytes are the same for both values (OldFieldTests).
    [Description("Only read when useLegacyMode is false: full dot density. No effect in legacy mode, the default. Leave it out. Default true.")]
    public bool HighDensity { get; set; } = true;
}
