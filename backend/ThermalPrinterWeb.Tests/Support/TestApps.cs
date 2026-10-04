using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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

    // Each host has its own print journal, in the temp directory. The default path is under the repo.
    public string JournalDirectory { get; } = Path.Combine(Path.GetTempPath(), "thermal-printer-web-tests", Guid.NewGuid().ToString("N"));

    protected virtual string JournalPathSetting => "Journal:DataPath";
    protected virtual string JournalPath => JournalDirectory;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        UseSettings(builder, (JournalPathSetting, JournalPath));
        builder.ConfigureServices(services =>
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs)));
        ConfigurePrinter(builder);
    }

    // Production defaults to the real printer: each host states which printer it has.
    protected abstract void ConfigurePrinter(IWebHostBuilder builder);

    // Added last, so these win over the settings files and the environment variables.
    protected static void UseSettings(IWebHostBuilder builder, params (string Key, string? Value)[] settings)
        => builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            settings.Select(setting => KeyValuePair.Create(setting.Key, setting.Value))));

    protected static void UseLoopbackPrinter(IWebHostBuilder builder, int port, TimeSpan? connectTimeout = null)
        => builder.ConfigureServices(services => services.Configure<PrinterOptions>(options =>
        {
            options.Address = $"{Loopback}:{port}";
            options.ConnectTimeout = connectTimeout ?? options.ConnectTimeout;
        }));

    protected static void UseRecordingPrinter(IWebHostBuilder builder, RecordingPrinter printer) => builder.ConfigureServices(services =>
    {
        services.RemoveAll<IPrinterService>();
        services.AddSingleton<IPrinterService>(printer);
    });

    // After the host stopped: the journal writer is done with its files.
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(JournalDirectory))
            Directory.Delete(JournalDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}

// The printer replaced: no test reaches the network.
public sealed class FakePrinterApp() : TestApp(Production)
{
    public RecordingPrinter Printer { get; } = new();

    protected override void ConfigurePrinter(IWebHostBuilder builder) => UseRecordingPrinter(builder, Printer);
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

// The real PrinterService with no printer: it builds each job and sends nothing.
internal sealed class NoPrinterApp() : TestApp("Development")
{
    // An address in the shell of the developer must not turn a test into a real print.
    protected override void ConfigurePrinter(IWebHostBuilder builder)
        => builder.ConfigureServices(services => services.Configure<PrinterOptions>(options => options.Address = string.Empty));
}
