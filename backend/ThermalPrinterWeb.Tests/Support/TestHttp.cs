using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ThermalPrinterWeb.Tests.Support;

// Requests to a test host: JSON over HTTP and JSON-RPC over /mcp.
internal static class TestHttp
{
    // The smallest print job that passes validation.
    public const string PrintJson = """{"content":[{"type":"Text","content":"x"}]}""";

    private const string McpUrl = "/mcp";

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public static async Task<(HttpStatusCode Status, string Body)> SendJsonAsync(
        this HttpClient client, HttpMethod method, string url, string? json = null, string? userAgent = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
            request.Content = Json(json);
        // Streamable HTTP (MCP) needs both; the controller ignores them.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (userAgent is not null)
            Assert.True(request.Headers.TryAddWithoutValidation("User-Agent", userAgent));

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // One JSON-RPC call. Returns the "result" member of the answer.
    public static async Task<JsonElement> McpAsync(this HttpClient client, string method, string? paramsJson = null, string? userAgent = null)
    {
        const string sseData = "data: ";
        var body = $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}"{{(paramsJson is null ? "" : $",\"params\":{paramsJson}")}}}""";
        var (status, text) = await client.SendJsonAsync(HttpMethod.Post, McpUrl, body, userAgent);
        Assert.Equal(HttpStatusCode.OK, status);

        // Streamable HTTP answers as one SSE event.
        var data = text.Split('\n').Single(line => line.StartsWith(sseData, StringComparison.Ordinal))[sseData.Length..];
        return JsonDocument.Parse(data).RootElement.GetProperty("result");
    }

    public static async Task<(bool IsError, string Text)> CallToolAsync(this HttpClient client, string tool, string? argumentsJson, string? userAgent = null)
    {
        var arguments = argumentsJson is null ? "" : $",\"arguments\":{argumentsJson}";
        var result = await client.McpAsync("tools/call", $$"""{"name":"{{tool}}"{{arguments}}}""", userAgent);

        var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
        return (isError, result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }
}
