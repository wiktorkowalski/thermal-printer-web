using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ThermalPrinterWeb.Mcp;

internal sealed class McpApiKeyMiddleware
{
    public const string McpPath = "/mcp";

    private const string BearerPrefix = "Bearer ";

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

        var apiKey = options.Value.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _apiKeyHash = Hash(apiKey);
        }
        else if (environment.IsDevelopment())
        {
            _allowWithoutKey = true;
            _logger.LogInformation("MCP API key not configured. {McpPath} is open in Development", McpPath);
        }
        else
        {
            // Fail closed, but do not throw: the HTTP print API must stay up.
            _logger.LogWarning(
                "MCP API key not configured. {McpPath} rejects every request. Set {ConfigPath}",
                McpPath,
                $"{McpAuthOptions.SectionName}:{nameof(McpAuthOptions.ApiKey)}");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(McpPath) || IsAuthorized(context.Request))
        {
            await _next(context);
            return;
        }

        _logger.LogWarning(
            "MCP auth failed for {Method} {Path} from {RemoteIp}",
            context.Request.Method,
            context.Request.Path,
            context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        // No WWW-Authenticate header: MCP clients read it as a prompt to start OAuth discovery.
        await context.Response.WriteAsync("Unauthorized", context.RequestAborted);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (_apiKeyHash is null)
            return _allowWithoutKey;

        var header = request.Headers.Authorization.ToString();

        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            && CryptographicOperations.FixedTimeEquals(Hash(header[BearerPrefix.Length..]), _apiKeyHash);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
