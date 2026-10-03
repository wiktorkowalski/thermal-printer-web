using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class TextBlockHandler : IBlockHandler
{
    public ContentType Type => ContentType.Text;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        ctx.AddRange(StyledText.Build(ctx, item.Content ?? string.Empty, item.Style));
        return Task.CompletedTask;
    }
}
