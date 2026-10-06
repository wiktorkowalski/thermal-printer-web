using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace ThermalPrinterWeb.Mcp;

// Fields of the print tool that change nothing on this printer (issue #44). The schema does not list them,
// so a caller does not learn them. The models keep them: an old payload and a stored job still bind,
// and the job has the same bytes as before.
internal static class HiddenPrintFields
{
    // The cutter makes a partial cut only.
    internal const string PartialCut = "partialCut";

    // The image bytes are the same with true and false in legacy mode, and legacy mode is the default.
    internal const string HighDensity = "highDensity";

    private const string Properties = "properties";

    public static void RemoveFrom(Tool printTool)
    {
        var schema = JsonNode.Parse(printTool.InputSchema.GetRawText());
        var block = schema?[Properties]?["content"]?["items"]?[Properties] as JsonObject;
        block?.Remove(PartialCut);
        (block?["imageOptions"]?[Properties] as JsonObject)?.Remove(HighDensity);
        printTool.InputSchema = JsonSerializer.SerializeToElement(schema);
    }
}
