namespace ThermalPrinterWeb.Mcp;

internal sealed class McpAuthOptions
{
    public const string SectionName = "McpServer";

    // Empty = /mcp open in Development, closed elsewhere. Set via env McpServer__ApiKey.
    public string ApiKey { get; set; } = "";
}
