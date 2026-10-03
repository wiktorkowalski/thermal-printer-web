using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Printing.Handlers;

internal sealed class SeparatorBlockHandler : IBlockHandler
{
    // The widest line: 64 characters in Font B (48 in Font A). More only wraps.
    internal const int MaxLength = 64;

    public ContentType Type => ContentType.Separator;

    public Task HandleAsync(PrintContent item, BlockContext ctx)
    {
        var separatorChar = item.SeparatorChar ?? "=";
        if (separatorChar.Length == 0)
            throw new PrintContentException("separatorChar must not be empty");

        var length = item.SeparatorLength ?? 32;
        if (length < 0)
            throw new PrintContentException("separatorLength must not be negative");
        if (length > MaxLength)
            throw PrintContentException.OverLimit("separatorLength", length, MaxLength);

        var sep = new string(separatorChar[0], length);
        ctx.AddRange(StyledText.Build(ctx, sep, item.Style));
        return Task.CompletedTask;
    }
}
