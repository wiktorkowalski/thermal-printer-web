using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class TextBlockHandler : IBlockHandler
{
    public ContentType Type => ContentType.Text;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        ctx.AddRange(StyledText.Build(ctx.Emitter, item.Content ?? string.Empty, item.Style, ctx.Encoding));
        return Task.CompletedTask;
    }
}
