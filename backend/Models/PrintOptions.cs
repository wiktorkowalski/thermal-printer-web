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
    // Null: the caller did not send it. Such a job has the bytes from before the field fed lines (issue #44): the cut command alone (CutFeed).
    [Description("Empty lines before each cut (a Cut block or the auto-cut), 0 to 255. One line is 29 dots (3.6 mm), or defaultLineSpacing dots when that is set. The lines count as paper. Left out: no empty line, and the cut goes through the last printed line; send 3 to keep that line whole.")]
    public int? FeedLinesAfterPrint { get; set; }
}
