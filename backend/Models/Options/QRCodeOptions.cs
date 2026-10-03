using System.ComponentModel;

namespace ThermalPrinterWeb.Models;

public class QRCodeOptions
{
    [Description("QR model. Content limit in UTF-8 bytes: Model2 2953 (default), Model1 707, Micro 21. Longer content is rejected.")]
    public QRCodeModel Model { get; set; } = QRCodeModel.Model2;
    [Description("Module size: Normal (default), Large or ExtraLarge.")]
    public QRCodeSize Size { get; set; } = QRCodeSize.Normal;
    [Description("Error correction: Percent7 (default), Percent15, Percent25 or Percent30. A higher level survives more damage and makes a denser code.")]
    public QRCodeCorrectionLevel CorrectionLevel { get; set; } = QRCodeCorrectionLevel.Percent7;
}
