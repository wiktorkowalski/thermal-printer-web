using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class SeparatorBlockHandler : IBlockHandler
{
    public ContentType Type => ContentType.Separator;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        var sep = new string((item.SeparatorChar ?? "=")[0], item.SeparatorLength ?? 32);
        ctx.AddRange(StyledText.Build(ctx.Emitter, sep, item.Style, ctx.Encoding));
        return Task.CompletedTask;
    }
}
