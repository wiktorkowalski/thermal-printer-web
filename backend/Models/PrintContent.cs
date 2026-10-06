using System.ComponentModel;

namespace ThermalPrinterWeb.Models;

public class PrintContent
{
    [Description("Block type. Text, Separator, LineFeed and Cut need no options; Barcode, QRCode and Image read 'content' plus their options object; CodePage switches to the code page named in 'content'; Signal sounds the buzzer or flashes the error light and prints nothing: add it only when the user asks for a sound or a light.")]
    public ContentType Type { get; set; }
    [Description("Text: the text to print, 48 characters per line (24 with DoubleWidth, 64 with FontB, 48 / size.width with 'size'); a longer line wraps in the middle of a word, \\n starts a new line (CR and CRLF count as \\n), a tab prints as a space, other control characters print as '?'; at most 10000 characters and 500 lines in one block. QRCode: the data, stored as UTF-8; \\n is a line break (CRLF counts as \\n), any other control character (tab and a CR with no \\n included) rejects the block. Barcode: the data, printable ASCII only; any other character rejects the block. Image: base64 PNG or JPEG, at most 16 MB, 16384 pixels per side and 50 megapixels (8192 x 6144 passes). CodePage: the code page name, for example PC852. Not used by LineFeed, Cut, Separator and Signal.")]
    public string? Content { get; set; }
    [Description("Horizontal position of the block. Default Center.")]
    public Alignment Alignment { get; set; } = Alignment.Center;
    [Description("Text and Separator styles; combine freely. DoubleWidth halves the characters per line (48 to 24), FontB is a smaller font with 64 per line, FontB with DoubleWidth gives 32. DoubleHeight does not change the line width. For text larger than 2x use 'size'.")]
    public List<PrintStyle>? Style { get; set; }
    [Description("Text and Separator blocks only: the character size as multipliers of 1 to 8, for example {\"width\":3,\"height\":3} for a headline of 16 characters per line. When present it replaces DoubleWidth and DoubleHeight in 'style'; FontB and the other styles still apply. Default: no size, 1 x 1 unless 'style' says otherwise.")]
    public TextSize? Size { get; set; }
    [Description("Barcode blocks only. Default: CODE128, 100 dots high, caption below.")]
    public BarcodeOptions? BarcodeOptions { get; set; }
    [Description("QRCode blocks only. Default: Model2, size Normal, correction Percent7.")]
    public QRCodeOptions? QRCodeOptions { get; set; }
    [Description("Image blocks only. Default: scale down to fit 576 x 576 dots, keep the aspect ratio.")]
    public ImageOptions? ImageOptions { get; set; }
    [Description("LineFeed blocks only: number of empty lines. Default 1, at most 100.")]
    public int? Lines { get; set; } = 1;
    // No description: it is not in the MCP schema (HiddenPrintFields). The cutter makes a partial cut for both values.
    // Still bound and sent as before.
    public bool? PartialCut { get; set; } = false;
    [Description("Separator blocks only: the character to repeat; only the first character is used and it must not be empty. Default '='.")]
    public string? SeparatorChar { get; set; } = "=";
    [Description("Separator blocks only: how many characters to print. 48 fills one line. Default 32, at most 64.")]
    public int? SeparatorLength { get; set; } = 32;
    [Description("Signal blocks only: the buzzer or the error light at this place in the document. Default: Sound, 1 beep, duration 1. One document holds at most 3 Signal blocks. Nothing beeps or lights by itself: add a Signal block only when the user asks for a sound or a light.")]
    public SignalOptions? SignalOptions { get; set; }
}
