using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Services;

namespace ThermalPrinterWeb.Tests.Support;

// The base of every test host: the real pipeline, with the log captured.
public abstract class TestApp(string environment) : WebApplicationFactory<Program>
{
    public const string Loopback = "127.0.0.1";
    protected const string Production = "Production";

    public RecordingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureServices(services =>
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs)));
        ConfigurePrinter(builder);
    }

    // Production defaults to the real printer: each host states which printer it has.
    protected abstract void ConfigurePrinter(IWebHostBuilder builder);

    protected static void UseLoopbackPrinter(IWebHostBuilder builder, int port, TimeSpan? connectTimeout = null)
        => builder.ConfigureServices(services => services.Configure<PrinterOptions>(options =>
        {
            options.Address = $"{Loopback}:{port}";
            options.ConnectTimeout = connectTimeout ?? options.ConnectTimeout;
        }));
}

// The printer replaced: no test reaches the network.
public sealed class FakePrinterApp() : TestApp(Production)
{
    public RecordingPrinter Printer { get; } = new();

    protected override void ConfigurePrinter(IWebHostBuilder builder) => builder.ConfigureServices(services =>
    {
        services.RemoveAll<IPrinterService>();
        services.AddSingleton<IPrinterService>(Printer);
    });
}

// The real PrinterService pointed at a loopback port.
internal sealed class LoopbackPrinterApp(int port, TimeSpan? connectTimeout = null) : TestApp(Production)
{
    protected override void ConfigurePrinter(IWebHostBuilder builder) => UseLoopbackPrinter(builder, port, connectTimeout);
}

// A request that passes validation by mistake must reach a closed loopback port only.
public sealed class ClosedPortApp() : TestApp(Production)
{
    private const int DiscardPort = 9;

    protected override void ConfigurePrinter(IWebHostBuilder builder) => UseLoopbackPrinter(builder, DiscardPort);
}
