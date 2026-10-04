using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ThermalPrinterWeb.Services;

namespace ThermalPrinterWeb.Mcp;

// Marks the MCP endpoint: a request that routing sends there needs the key, whatever its path looks like.
internal sealed class McpEndpointAttribute : Attribute;

// Guards every request to /mcp with "Authorization: Bearer <key>". It runs after routing and before the journal
// middleware: a rejected request reaches no tool, gets no journal row and its body is not read.
// The key and the value a caller sent go to no log and no answer.
internal sealed class McpApiKeyMiddleware
{
    public const string McpPath = "/mcp";

    internal const string Unauthorized = "Unauthorized";

    private const string BearerPrefix = "Bearer ";
    private const int MaxLoggedMethodLength = 16;
    private const int MaxLoggedPathLength = 64;

    private readonly RequestDelegate _next;
    private readonly ILogger<McpApiKeyMiddleware> _logger;

    // Hashes have one length, so the compare leaks no key length.
    private readonly byte[]? _apiKeyHash;
    private readonly bool _allowWithoutKey;

    public McpApiKeyMiddleware(
        RequestDelegate next,
        IOptions<McpAuthOptions> options,
        IHostEnvironment environment,
        ILogger<McpApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        // A space or a line end around the key in an env file is not part of it.
        var apiKey = options.Value.ApiKey?.Trim();
        if (!string.IsNullOrEmpty(apiKey))
        {
            _apiKeyHash = Hash(apiKey);
        }
        else if (environment.IsDevelopment())
        {
            _allowWithoutKey = true;
            _logger.LogInformation("MCP API key is not set: {McpPath} is open in Development", McpPath);
        }
        else
        {
            // Fail closed, and do not stop the app: the HTTP print API must stay up.
            _logger.LogWarning(
                "MCP API key is not set: {McpPath} rejects every request. Set {Setting}",
                McpPath,
                $"{McpAuthOptions.SectionName}:{nameof(McpAuthOptions.ApiKey)}");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsMcp(context) || IsAuthorized(context.Request))
        {
            await _next(context);
            return;
        }

        // Method and path are caller text. No header goes to the log.
        _logger.LogWarning(
            "MCP auth failed: {Method} {Path} from {RemoteIp}",
            LogSafeText.Clean(context.Request.Method, MaxLoggedMethodLength),
            LogSafeText.Clean(context.Request.Path.Value, MaxLoggedPathLength),
            context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        // No WWW-Authenticate header: MCP clients read it as a prompt to start OAuth discovery.
        await context.Response.WriteAsync(Unauthorized, context.RequestAborted);
    }

    // The path check also covers a path under /mcp that no endpoint serves. It ignores letter case, and the path is decoded.
    private static bool IsMcp(HttpContext context)
        => context.Request.Path.StartsWithSegments(McpPath)
            || context.GetEndpoint()?.Metadata.GetMetadata<McpEndpointAttribute>() is not null;

    private bool IsAuthorized(HttpRequest request)
    {
        if (_apiKeyHash is null)
            return _allowWithoutKey;

        // Two Authorization headers come as one text with a comma: no match.
        var header = request.Headers.Authorization.ToString();

        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            && CryptographicOperations.FixedTimeEquals(Hash(header[BearerPrefix.Length..]), _apiKeyHash);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
