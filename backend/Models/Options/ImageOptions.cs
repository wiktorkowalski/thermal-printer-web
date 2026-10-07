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

    // The two below: accepted and not read (issue #44). The server always prints an image with the legacy raster command;
    // the other command prints garbage text on this printer (read from paper on 2026-10-07).
    // No description: they are not in the MCP schema (HiddenPrintFields). Nullable, so that a null from a caller binds.
    // They stay in the model: callers and journal rows hold them, and a new row keeps the shape of the old ones.
    public bool? UseLegacyMode { get; set; } = true;
    public bool? HighDensity { get; set; } = true;
}
