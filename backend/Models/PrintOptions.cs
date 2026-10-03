using System.ComponentModel;

namespace ThermalPrinterWeb.Models;

public class PrintOptions
{
    [Description("Code page for all text. Default PC852 (Polish and Central European letters). Also: PC437, PC850, PC858, WPC1252, WPC1250, ISO8859_2. A character outside the code page prints as '?'.")]
    public string? CodePage { get; set; }
    [Description("Line spacing in dots, 0 to 255. Leave it out to keep the printer default.")]
    public int? DefaultLineSpacing { get; set; }
    [Description("Cut the paper after the last block when the content has no Cut block. Default true.")]
    public bool AutoCut { get; set; } = true;
    [Description("Paper fed before a cut, so the last line clears the cutter, 0 to 255. Default 3.")]
    public int FeedLinesAfterPrint { get; set; } = 3;
}
