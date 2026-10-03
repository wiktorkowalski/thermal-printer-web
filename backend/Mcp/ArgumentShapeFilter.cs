using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ThermalPrinterWeb.Mcp;

// Without this the SDK answers "An error occurred invoking '<tool>'" and logs the caller's mistake as an error.
internal static class ArgumentShapeFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken);
            }
            catch (ToolArgumentException ex)
            {
                return Reject(context, ex.Tool, ex.Problem);
            }
            catch (JsonException ex) when (context.Params?.Name is { } tool)
            {
                // SDK argument binding failed. The path holds schema property names only; the message can repeat caller content.
                var problem = ex.Path is null or "$"
                    ? "an argument has the wrong JSON type"
                    : $"the value at {ex.Path} has the wrong JSON type or is not a known name";
                return Reject(context, tool, problem);
            }
        };

    private static CallToolResult Reject(RequestContext<CallToolRequestParams> context, string tool, string problem)
    {
        // The caller's mistake, not a fault: Information. No caller content.
        context.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(ArgumentShapeFilter))
            .LogInformation("Rejected MCP call to {Tool}: {Problem}", tool, problem);

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = PrinterTools.WrongArgumentsMessage(tool, problem) }]
        };
    }
}
