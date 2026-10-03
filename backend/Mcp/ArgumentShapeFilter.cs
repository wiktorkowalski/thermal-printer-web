using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ThermalPrinterWeb.Mcp;

// Answers a wrong-shaped tool call with the correct shape. Without it the SDK
// replies "An error occurred invoking '<tool>'" and logs the caller's mistake
// as an error.
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
                // The SDK binds arguments before the tool body runs; a wrong JSON type
                // fails there. The path is relative to the argument and holds schema
                // property names only. The exception message is not used.
                var where = ex.Path is null or "$" ? "an argument" : $"the value at {ex.Path}";
                return Reject(context, tool, $"{where} has the wrong JSON type");
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
            Content = [new TextContentBlock { Text = ToolArgumentException.MessageFor(tool, problem) }]
        };
    }
}
