using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class SeparatorBlockHandler : IBlockHandler
{
    public ContentType Type => ContentType.Separator;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        var separatorChar = item.SeparatorChar ?? "=";
        if (separatorChar.Length == 0)
            throw new PrintContentException("separatorChar must not be empty");

        var length = item.SeparatorLength ?? 32;
        if (length < 0)
            throw new PrintContentException("separatorLength must not be negative");

        var sep = new string(separatorChar[0], length);
        ctx.AddRange(StyledText.Build(ctx, sep, item.Style));
        return Task.CompletedTask;
    }
}
