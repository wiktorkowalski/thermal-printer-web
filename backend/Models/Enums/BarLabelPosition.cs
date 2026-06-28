using System.Text.Json.Serialization;

namespace ThermalPrinterWeb.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BarLabelPosition
{
    None,
    Above,
    Below,
    Both
}
