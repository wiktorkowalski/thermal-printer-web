namespace ThermalPrinterWeb.Models;

public class PrintContent
{
    public ContentType Type { get; set; }
    public string? Content { get; set; }
    public Alignment Alignment { get; set; } = Alignment.Center;
    public List<PrintStyle>? Style { get; set; }
    public BarcodeOptions? BarcodeOptions { get; set; }
    public QRCodeOptions? QRCodeOptions { get; set; }
    public ImageOptions? ImageOptions { get; set; }
    public int? Lines { get; set; } = 1;
    public bool? PartialCut { get; set; } = false;
    public string? SeparatorChar { get; set; } = "=";
    public int? SeparatorLength { get; set; } = 32;
}
