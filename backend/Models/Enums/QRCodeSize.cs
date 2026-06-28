using System.Text.Json.Serialization;

namespace ThermalPrinterWeb.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QRCodeSize
{
    Normal,
    Large,
    ExtraLarge
}
