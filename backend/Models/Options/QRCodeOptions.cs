namespace ThermalPrinterWeb.Models;

public class QRCodeOptions
{
    public QRCodeModel Model { get; set; } = QRCodeModel.Model2;
    public QRCodeSize Size { get; set; } = QRCodeSize.Normal;
    public QRCodeCorrectionLevel CorrectionLevel { get; set; } = QRCodeCorrectionLevel.Percent7;
}
