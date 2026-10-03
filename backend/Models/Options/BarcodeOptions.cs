using System.ComponentModel;

namespace ThermalPrinterWeb.Models;

public class BarcodeOptions
{
    [Description("Barcode symbology. Default CODE128. The content must be valid for the type and printable ASCII; CODE128 holds at most 253 characters, fewer when the content has a curly bracket.")]
    public BarcodeType Type { get; set; } = BarcodeType.CODE128;
    [Description("Bar height in dots (8 dots = 1 mm), 1 to 255. Default 100.")]
    public int? HeightInDots { get; set; } = 100;
    [Description("Bar width: Thin, Default or Thick.")]
    public BarWidth? Width { get; set; } = BarWidth.Default;
    [Description("Where the readable caption prints: None, Above, Below (default) or Both.")]
    public BarLabelPosition? LabelPosition { get; set; } = BarLabelPosition.Below;
    [Description("Print the caption in the smaller FontB. Default false.")]
    public bool? UseFontB { get; set; } = false;
}
