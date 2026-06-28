using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class CodePageBlockHandler(ILogger<CodePageBlockHandler> logger) : IBlockHandler
{
    public ContentType Type => ContentType.CodePage;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        if (!string.IsNullOrEmpty(item.Content))
        {
            var cp = CodePages.Resolve(item.Content);
            if (cp.HasValue)
            {
                ctx.Add(ctx.Emitter.CodePage(cp.Value));
                ctx.Encoding = CodePages.GetEncoding(item.Content);
            }
            else
            {
                logger.LogWarning("Unknown code page {CodePage} in content, keeping current encoding", item.Content);
            }
        }
        return Task.CompletedTask;
    }
}
