using ModelContextProtocol;

namespace ThermalPrinterWeb.Mcp;

// An McpException, so the SDK still sends the message if ArgumentShapeFilter is not registered. The problem text must not repeat caller content.
internal sealed class ToolArgumentException(string tool, string problem)
    : McpException(PrinterTools.WrongArgumentsMessage(tool, problem))
{
    public string Tool { get; } = tool;
    public string Problem { get; } = problem;
}
