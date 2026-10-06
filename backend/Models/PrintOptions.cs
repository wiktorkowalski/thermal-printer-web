using System.ComponentModel;

namespace ThermalPrinterWeb.Models;

public class PrintOptions
{
    [Description("Code page for all text. Default PC852 (Polish and Central European letters). Also: PC437, PC850, PC858, WPC1252; Polish letters print only with PC852. A character outside the code page prints as '?'.")]
    public string? CodePage { get; set; }
    [Description("Line spacing in dots, 0 to 255. Leave it out to keep the printer default.")]
    public int? DefaultLineSpacing { get; set; }
    [Description("Cut the paper after the last block when the content has no Cut block. Default true.")]
    public bool AutoCut { get; set; } = true;
    // The name says lines; the printer reads the n of GS V as motion units. A rename or a conversion changes the API or the bytes (issue #44).
    [Description("Extra feed before a cut in printer motion units of at most 0.125 mm, 0 to 255. Not lines: the default 3 is under 1 mm. For empty lines before the cut add a LineFeed block. Leave it out.")]
    public int FeedLinesAfterPrint { get; set; } = 3;
}
