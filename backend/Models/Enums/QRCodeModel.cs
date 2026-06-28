using System.Text.Json.Serialization;

namespace ThermalPrinterWeb.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QRCodeModel
{
    Model1,
    Model2,
    Micro
}
