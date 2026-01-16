namespace ThermalPrinterWeb.Models;

public class ImageOptions
{
    public int? MaxWidth { get; set; } = 500;
    public int? MaxHeight { get; set; } = 500;
    public bool PreserveAspectRatio { get; set; } = true;
    public bool UseLegacyMode { get; set; } = true;
}
