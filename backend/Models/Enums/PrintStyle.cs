using System.Text.Json.Serialization;

namespace ThermalPrinterWeb.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrintStyle
{
    Normal,
    Bold,
    Italic,
    Underline,
    DoubleHeight,
    DoubleWidth,
    FontB,
    ReverseMode,
    UpsideDownMode
}
