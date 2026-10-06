using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace ThermalPrinterWeb.Mcp;

// A field of the print tool that changes nothing on this printer (issue #44). The schema does not list it,
// so a caller does not learn it. The model keeps it: an old payload and a stored job still bind,
// and the job has the same bytes as before.
internal static class HiddenPrintFields
{
    // The cutter makes a partial cut only.
    internal const string PartialCut = "partialCut";

    private const string Properties = "properties";

    // A schema of another shape loses nothing and the app starts: OldFieldTests fails then.
    public static void RemoveFrom(IEnumerable<McpServerTool> tools)
    {
        var printTool = tools.FirstOrDefault(tool => tool.ProtocolTool.Name == PrinterTools.PrintName)?.ProtocolTool;
        if (printTool is null)
            return;

        var schema = JsonNode.Parse(printTool.InputSchema.GetRawText());
        if (schema?[Properties]?["content"]?["items"]?[Properties] is not JsonObject block || !block.Remove(PartialCut))
            return;

        printTool.InputSchema = JsonSerializer.SerializeToElement(schema);
    }
}
