namespace ThermalPrinterWeb.Mcp;

internal sealed class McpAuthOptions
{
    public const string SectionName = "McpServer";

    // Setting McpServer:ApiKey, env McpServer__ApiKey. Empty: /mcp is open in Development and closed in every other environment.
    // The value is a secret: it goes to no log, no answer and no exception message.
    public string ApiKey { get; set; } = "";
}
