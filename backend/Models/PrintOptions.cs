namespace ThermalPrinterWeb.Models;

public class PrintOptions
{
    public string? CodePage { get; set; }
    public int? DefaultLineSpacing { get; set; }
    public bool AutoCut { get; set; } = true;
    public int FeedLinesAfterPrint { get; set; } = 3;
}
