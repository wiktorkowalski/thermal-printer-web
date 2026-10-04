using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;

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
                // SDK argument binding failed. The exception message can repeat caller content: only the path goes out.
                var path = ex.Path?.StartsWith(JsonPathRoot, StringComparison.Ordinal) == true ? ex.Path : JsonPathRoot;
                return Reject(context, tool, DescribeProblem(context, path));
            }
        };

    // The SDK reads each argument on its own, so the path starts at the argument. The exception does not name the argument.
    private static string DescribeProblem(RequestContext<CallToolRequestParams> context, string path)
    {
        var argument = FaultyArgument(context, path);
        if (path == JsonPathRoot)
            return argument is null ? "an argument has the wrong JSON type" : $"'{argument}' has the wrong JSON type";

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

        // At the root the value itself does not fit the schema type. Below the root the argument is an array or an object.
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
            ? type.EnumerateArray().Any(allowed => Accepts(allowed, value))
            : Accepts(type, value);
    }

    private static bool Accepts(JsonElement schemaType, JsonElement value) => schemaType.GetString() switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => IsNumber(value, wholeOnly: true),
        "number" => IsNumber(value, wholeOnly: false),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        // A schema type not known here: no claim.
        _ => true
    };

    // The SDK also reads a number from a string: "5" is a valid count.
    private static bool IsNumber(JsonElement value, bool wholeOnly) => value.ValueKind switch
    {
        JsonValueKind.Number => !wholeOnly || value.TryGetInt64(out _),
        JsonValueKind.String => wholeOnly
            ? long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            : double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        _ => false
    };

    private static string? SourceArgument(RequestContext<CallToolRequestParams> context)
        => context.Params?.Arguments is { } arguments
            && arguments.TryGetValue("source", out var source)
            && source.ValueKind == JsonValueKind.String
                ? source.GetString()
                : null;

    private static CallToolResult Reject(RequestContext<CallToolRequestParams> context, string tool, string problem)
    {
        // The caller's mistake, not a fault: Information. No caller content.
        context.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(ArgumentShapeFilter))
            .LogInformation("Rejected MCP call to {Tool}: {Problem}", tool, problem);

        // A print call with the wrong shape is a refused job: the journal stores the request. No "Print job:" line.
        if (tool is PrinterTools.PrintName or PrinterTools.PrintNoteName)
        {
            PrintJobTrace.Current?.Outcome = new PrintJobOutcome(
                PrintJobLog.McpTransport(tool), SourceArgument(context), PrintResult.Invalid(problem), Content: null, Options: null);
        }

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = PrinterTools.WrongArgumentsMessage(tool, problem) }]
        };
    }
}
