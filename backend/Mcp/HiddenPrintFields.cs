using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing;

namespace ThermalPrinterWeb.Mcp;

// A field or an enum name of the print tool that changes nothing on this printer (issue #44). The schema does not list it,
// so a caller does not learn it. The model keeps it: an old payload and a stored job still bind,
// and the job has the same bytes as before. One exception: a job with useLegacyMode false now prints its image in legacy mode.
internal static class HiddenPrintFields
{
    // The cutter makes a partial cut only.
    internal const string PartialCut = "partialCut";

    // The server always prints an image in legacy mode: the handler reads neither field.
    internal const string UseLegacyMode = "useLegacyMode";
    internal const string HighDensity = "highDensity";

    private const string Properties = "properties";
    private const string EnumNames = "enum";

    // A schema of another shape loses nothing and the app starts: OldFieldTests fails then.
    public static void RemoveFrom(IEnumerable<McpServerTool> tools)
    {
        var printTool = tools.FirstOrDefault(tool => tool.ProtocolTool.Name == PrinterTools.PrintName)?.ProtocolTool;
        if (printTool is null)
            return;

        var schema = JsonNode.Parse(printTool.InputSchema.GetRawText());
        if (Child(Child(Child(Child(schema, Properties), "content"), "items"), Properties) is not { } block)
            return;

        var changed = block.Remove(PartialCut);
        if (Child(Child(block, "imageOptions"), Properties) is { } imageOptions)
        {
            changed |= imageOptions.Remove(UseLegacyMode);
            changed |= imageOptions.Remove(HighDensity);
        }

        // The members of no effect: BlockEnums names them.
        changed |= RemoveNames(Child(Child(block, "style"), "items")?[EnumNames], BlockEnums.NoEffectNames<PrintStyle>());
        changed |= RemoveNames(Child(Child(Child(block, "barcodeOptions"), Properties), "type")?[EnumNames], BlockEnums.NoEffectNames<BarcodeType>());

        if (changed)
            printTool.InputSchema = JsonSerializer.SerializeToElement(schema);
    }

    // The indexer of a node that is not an object throws: a schema such as "items": true must not stop the app.
    private static JsonObject? Child(JsonNode? node, string name) => (node as JsonObject)?[name] as JsonObject;

    private static bool RemoveNames(JsonNode? names, string[] hidden)
    {
        if (names is not JsonArray array)
            return false;

        var changed = false;
        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (array[i] is JsonValue value && value.TryGetValue<string>(out var name) && hidden.Contains(name))
            {
                array.RemoveAt(i);
                changed = true;
            }
        }

        return changed;
    }
}
