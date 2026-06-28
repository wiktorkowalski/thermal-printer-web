using System.Text.Json.Serialization;

namespace ThermalPrinterWeb.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BarcodeType
{
    UPC_A,
    UPC_E,
    EAN13,
    EAN8,
    CODE39,
    CODE128,
    ITF,
    CODABAR,
    GS1_128,
    GS1_DATABAR_OMNIDIRECTIONAL
}
