using System.Text.Json.Serialization;

namespace ThermalPrinterWeb.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QRCodeCorrectionLevel
{
    Percent7,
    Percent15,
    Percent25,
    Percent30
}
