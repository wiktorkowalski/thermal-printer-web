using System.Text.Json.Serialization;

namespace ThermalPrinterWeb.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BarWidth
{
    Thin,
    Default,
    Thick
}
