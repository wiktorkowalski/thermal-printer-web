using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ThermalPrinterWeb.Mcp;

// Without this the SDK answers "An error occurred invoking '<tool>'" and logs the caller's mistake as an error.
internal static class ArgumentShapeFilter
{
    private const string JsonPathRoot = "$";

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
                return Reject(context, tool, Problem(context, ex.Path ?? JsonPathRoot));
            }
        };

    // The SDK reads each argument on its own: the path starts at the argument and the exception does not name it.
    private static string Problem(RequestContext<CallToolRequestParams> context, string path)
    {
        var argument = FaultyArgument(context, path);
        if (path == JsonPathRoot)
            return argument is null ? "an argument has the wrong JSON type" : $"'{argument}' has the wrong JSON type";

        // "$[0].type" in 'content' reads "content[0].type".
        var place = argument is null ? path : argument + path[JsonPathRoot.Length..];
        return $"the value at {place} has the wrong JSON type or is not a known name";
    }

    // Null when no argument or more than one fits: a wrong name is worse than no name.
    private static string? FaultyArgument(RequestContext<CallToolRequestParams> context, string path)
    {
        if (context.Params?.Arguments is not { } arguments
            || context.MatchedPrimitive is not McpServerTool { ProtocolTool.InputSchema: var schema }
            || !schema.TryGetProperty("properties", out var properties))
        {
            return null;
        }

        // The root: the value itself has a type the schema does not allow.
        // Below the root the path starts with an index or a property: the argument is an array or an object.
        var atRoot = path == JsonPathRoot;
        var container = path.StartsWith(JsonPathRoot + "[", StringComparison.Ordinal) ? JsonValueKind.Array : JsonValueKind.Object;

        // Schema argument names only: an argument name the caller made up is caller content.
        var candidates = arguments
            .Where(argument => properties.TryGetProperty(argument.Key, out var property)
                && (atRoot ? !MatchesSchemaType(argument.Value, property) : argument.Value.ValueKind == container))
            .Select(argument => argument.Key)
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool MatchesSchemaType(JsonElement value, JsonElement schema)
    {
        // No type in the schema: no claim.
        if (!schema.TryGetProperty("type", out var type))
            return true;

        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(allowed => Accepts(allowed, value.ValueKind))
            : Accepts(type, value.ValueKind);
    }

    private static bool Accepts(JsonElement schemaType, JsonValueKind kind) => schemaType.GetString() switch
    {
        "string" => kind == JsonValueKind.String,
        "integer" or "number" => kind == JsonValueKind.Number,
        "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
        "array" => kind == JsonValueKind.Array,
        "object" => kind == JsonValueKind.Object,
        "null" => kind == JsonValueKind.Null,
        // A schema type not known here: no claim.
        _ => true
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
