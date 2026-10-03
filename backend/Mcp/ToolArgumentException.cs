using ModelContextProtocol;

namespace ThermalPrinterWeb.Mcp;

// Thrown by a tool body when the caller sent arguments in the wrong shape.
// ArgumentShapeFilter turns it into the tool result. The problem text must not
// repeat caller content. An McpException, so the SDK still sends the message to
// the caller if the filter is not registered.
internal sealed class ToolArgumentException(string tool, string problem)
    : McpException(MessageFor(tool, problem))
{
    public string Tool { get; } = tool;
    public string Problem { get; } = problem;

    // Names the problem and shows one valid call.
    public static string MessageFor(string tool, string problem)
        => PrinterTools.ValidCallFor(tool) is { } validCall
            ? $"Wrong arguments for '{tool}': {problem}. Example of a valid call: {validCall}"
            : $"Wrong arguments for '{tool}': {problem}.";
}
