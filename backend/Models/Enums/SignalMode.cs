using System.Text.Json.Serialization;

namespace ThermalPrinterWeb.Models;

// The number is the n byte of ESC C m t n. The name is the API contract: journal rows store it.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignalMode
{
    Sound = 1,
    Light = 2,
    SoundAndLight = 3
}
