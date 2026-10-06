using System.ComponentModel;

namespace ThermalPrinterWeb.Models;

public class BarcodeOptions
{
    // The numbers are BarcodeWidth: BarcodeWidthTests pins them. No text names the GS1 types: they print no bars (BlockEnums.HasNoEffect).
    [Description("Barcode symbology. Default CODE128. The content must be valid for the type and printable ASCII. A barcode wider than the paper (576 dots) is rejected, because the printer drops it with no error: CODE128 holds 9 characters at width Default, 14 at Thin and 7 at Thick. Read from paper so far: 8 characters at Default and 10 at Thin print. EAN13, EAN8, UPC_A and UPC_E always fit. For CODE39, ITF and CODABAR the check is a lower bound: a barcode that passes can still be too wide, so prefer CODE128. For more data use a QRCode block.")]
    public BarcodeType Type { get; set; } = BarcodeType.CODE128;
    [Description("Bar height in dots (8 dots = 1 mm), 1 to 255. Default 100.")]
    public int? HeightInDots { get; set; } = 100;
    [Description("Width of the narrowest bar: Thin (3 dots), Default (4 dots) or Thick (5 dots). The whole barcode must fit the 576 dots of the paper, else the block is rejected: Thin holds the most data.")]
    public BarWidth? Width { get; set; } = BarWidth.Default;
    [Description("Where the readable caption prints: None, Above, Below (default) or Both.")]
    public BarLabelPosition? LabelPosition { get; set; } = BarLabelPosition.Below;
    [Description("Print the caption in the smaller FontB. Default false.")]
    public bool? UseFontB { get; set; } = false;
}
