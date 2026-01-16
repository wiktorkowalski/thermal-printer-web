namespace ThermalPrinterWeb.Models;

public class BarcodeOptions
{
    public BarcodeType Type { get; set; } = BarcodeType.CODE128;
    public int? HeightInDots { get; set; } = 100;
    public BarWidth? Width { get; set; } = BarWidth.Default;
    public BarLabelPosition? LabelPosition { get; set; } = BarLabelPosition.Below;
    public bool? UseFontB { get; set; } = false;
}
