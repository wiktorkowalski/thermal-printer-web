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
    // Null: the caller did not send it. The server then keeps CutFeed.DefaultLines empty lines before each cut (issue #44). A test pins the 3 of the text.
    [Description("Empty lines before each cut (a Cut block or the auto-cut), 0 to 255. One line is 29 dots (3.6 mm), or defaultLineSpacing dots when that is set. The lines count as paper. Left out: the server keeps 3 empty lines before the cut, and the lines of a LineFeed block right before the cut count toward the 3. A number is added as sent, whatever the content; 0 adds no line, and with no empty line the cut goes through the last printed line.")]
    public int? FeedLinesAfterPrint { get; set; }
    // The server builds the line (SignatureLine); PrinterService does not read the field. A test pins the 19 of the text.
    [Description("Signature line. A name, for example \"Claude\": the server adds one last text line \"yyyy-MM-dd * name\" with the date of the print in Poland, in the house style of the date line (FontB, size 2x3, right), after the last printed block and before the empty lines and the cut. So do not type a date line yourself. At most 19 characters, one line (a character that prints as two, such as an arrow, counts as two); a control character rejects the document. Left out or empty: no line.")]
    public string? Sign { get; set; }
}
