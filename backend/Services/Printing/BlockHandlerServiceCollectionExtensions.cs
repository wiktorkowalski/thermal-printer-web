using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Services.Printing;

internal static class BlockHandlerServiceCollectionExtensions
{
    // One singleton per block type. Handlers are stateless - per-document state
    // lives in the BlockContext passed to HandleAsync - so they match the
    // singleton PrinterService lifetime without a captive-dependency warning.
    public static IServiceCollection AddPrinterBlockHandlers(this IServiceCollection services)
    {
        services.AddSingleton<IBlockHandler, TextBlockHandler>();
        services.AddSingleton<IBlockHandler, ImageBlockHandler>();
        services.AddSingleton<IBlockHandler, BarcodeBlockHandler>();
        services.AddSingleton<IBlockHandler, QRCodeBlockHandler>();
        services.AddSingleton<IBlockHandler, LineFeedBlockHandler>();
        services.AddSingleton<IBlockHandler, CutBlockHandler>();
        services.AddSingleton<IBlockHandler, SeparatorBlockHandler>();
        services.AddSingleton<IBlockHandler, CodePageBlockHandler>();
        return services;
    }
}
