using System.Text.Json.Serialization;

namespace ThermalPrinterWeb.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContentType
{
    Text,
    Image,
    Barcode,
    QRCode,
    LineFeed,
    Cut,
    Separator,
    CodePage
}
