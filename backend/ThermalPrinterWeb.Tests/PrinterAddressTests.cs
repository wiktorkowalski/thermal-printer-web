using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThermalPrinterWeb.Services;

namespace ThermalPrinterWeb.Tests;

// Where the printer address comes from in each environment. No test here opens a connection to the printer.
public sealed class PrinterAddressTests
{
    private const string ProductionAddress = "192.168.123.100:9100";
    private const string AddressVariable = "Printer__Address";
    private static readonly TimeSpan EntryPointTimeout = TimeSpan.FromSeconds(30);
    private const string PrintJson = TestHttp.PrintJson;

    // The app with the real PrinterService and the settings files of the repo.
    private sealed class App(string environment, string? address = null) : TestApp(environment)
    {
        // The real settings, on purpose: in Production this host has the address of the real printer. No test here sends to it.
        protected override void ConfigurePrinter(IWebHostBuilder builder)
        {
            if (address is not null)
            {
                builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Printer:Address"] = address }));
            }
        }

        public string Address => Services.GetRequiredService<IOptions<PrinterOptions>>().Value.Address;

        public List<string> InformationLogs(string start)
            => [.. Logs.Entries.Where(entry => entry.Level == LogLevel.Information && entry.Message.StartsWith(start, StringComparison.Ordinal)).Select(entry => entry.Message)];
    }

    private static PrinterOptionsValidator Validator(string environment)
        => new(new HostingEnvironment { EnvironmentName = environment });

    [Fact]
    public void Production_WithNoSetting_UsesThePrinterOnTheHomeNetwork()
    {
        using var app = new App(Environments.Production);

        Assert.Equal(ProductionAddress, app.Address);
        Assert.Equal([$"Printer target: {ProductionAddress}"], app.InformationLogs("Printer target"));
    }

    // The container must print when appsettings.json is missing or replaced.
    [Fact]
    public void CodeDefault_IsThePrinterOnTheHomeNetwork()
    {
        var options = new PrinterOptions();

        Assert.Equal(ProductionAddress, options.Address);
        Assert.True(options.HasPrinter);
        Assert.Equal(TimeSpan.FromSeconds(3), options.ConnectTimeout);
    }

    [Fact]
    public async Task Development_HasNoPrinter_StatusPrintAndBeepAnswerAndSendNothing()
    {
        using var app = new App(Environments.Development);
        // Before the first request: a Printer__Address in the shell of the developer must not turn this test into a real print.
        Assert.Equal("", app.Address);
        var client = app.CreateClient();

        var status = await client.GetAsync("/api/printer/status");
        var print = await client.PostAsync("/api/printer", TestHttp.Json(PrintJson));
        var beep = await client.PostAsync("/api/printer/beep?count=2&duration=3", null);

        Assert.Equal(["Printer target: no printer (Development); print and beep send nothing"], app.InformationLogs("Printer target"));

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(
            """{"reachable":true,"online":true,"coverOpen":false,"paperOut":false,"paperLow":false,"raw":"no printer","cutterError":false,"unrecoverableError":false,"autoRecoverableError":false,"recoverableError":false,"ready":true,"notReadyReason":null}""",
            await status.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, print.StatusCode);
        Assert.Equal(HttpStatusCode.OK, beep.StatusCode);
        var dropped = app.InformationLogs("No printer configured");
        Assert.Equal(2, dropped.Count);
        Assert.Matches(@"^No printer configured: print job of \d+ bytes not sent$", dropped[0]);
        Assert.Equal("No printer configured: signal command of 4 bytes not sent", dropped[1]);
        // No connection attempt: a failed one logs a warning.
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Level >= LogLevel.Warning && entry.Category == typeof(PrinterService).FullName);
    }

    // On Linux the file lookup is case-sensitive: "development" does not find appsettings.Development.json.
    [Fact]
    public void Development_WithoutItsSettingsFile_HasNoPrinter()
    {
        using var app = new App("development");

        Assert.Equal("", app.Address);
        Assert.Equal(["Printer target: no printer (development); print and beep send nothing"], app.InformationLogs("Printer target"));
    }

    [Fact]
    public void Development_WithAnAddressSetOnPurpose_UsesIt()
    {
        using var app = new App(Environments.Development, "127.0.0.1:9100");

        Assert.Equal("127.0.0.1:9100", app.Address);
        Assert.Equal(["Printer target: 127.0.0.1:9100"], app.InformationLogs("Printer target"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void EnvironmentVariable_OverridesTheSettingsFile(string environment)
    {
        var before = Environment.GetEnvironmentVariable(AddressVariable);
        Environment.SetEnvironmentVariable(AddressVariable, "printer.test");
        try
        {
            using var app = new App(environment);

            Assert.Equal("printer.test", app.Address);
            // The port is optional.
            Assert.Equal(["Printer target: printer.test:9100"], app.InformationLogs("Printer target"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AddressVariable, before);
        }
    }

    [Theory]
    [InlineData("", "Printer:Address is empty")]
    [InlineData("192.168.123.100:99999", "Printer:Address \"192.168.123.100:99999\" is not valid")]
    [InlineData("http://192.168.123.100:9100", "is not valid")]
    public async Task Production_WithABadAddress_DoesNotStart(string address, string message)
    {
        var (exitCode, stderr) = await RunEntryPointAsync($"--Printer:Address={address}");

        Assert.Equal(1, exitCode);
        Assert.StartsWith("Printer settings are not valid, the app does not start: ", stderr);
        Assert.Contains(message, stderr);
        Assert.Contains("host or host:port", stderr);
    }

    [Theory]
    [InlineData("3", "outside the range")]
    [InlineData("3s", "Printer:ConnectTimeout")]
    public async Task Production_WithABadConnectTimeout_DoesNotStart(string timeout, string message)
    {
        var (exitCode, stderr) = await RunEntryPointAsync($"--Printer:ConnectTimeout={timeout}");

        Assert.Equal(1, exitCode);
        Assert.Contains(message, stderr);
    }

    // The real entry point. With a bad setting it returns before the server listens.
    private static async Task<(int ExitCode, string Stderr)> RunEntryPointAsync(string setting)
    {
        var stderr = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            // The time limit: an accepted setting starts the server, and the call never returns.
            var exitCode = await Task
                .Run(() => (int)typeof(Program).Assembly.EntryPoint!.Invoke(null, [new[] { "--environment=Production", setting }])!)
                .WaitAsync(EntryPointTimeout);
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetError(stderr);
        }
    }

    [Theory]
    [InlineData("192.168.123.100:9100", "192.168.123.100", 9100)]
    [InlineData("192.168.123.100", "192.168.123.100", 9100)]
    [InlineData("printer.local:1", "printer.local", 1)]
    [InlineData(" localhost:65535 ", "localhost", 65535)]
    public void TryParse_ValidAddress_GivesHostAndPort(string address, string host, int port)
    {
        Assert.True(PrinterEndpoint.TryParse(address, out var endpoint));

        Assert.Equal(new PrinterEndpoint(host, port), endpoint);
        Assert.Equal($"{host}:{port}", endpoint.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(":9100")]
    [InlineData("host:")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:-1")]
    [InlineData("host:+9100")]
    [InlineData("host: 9100")]
    [InlineData("host:port")]
    [InlineData("host:9100:1")]
    [InlineData("::1")]
    [InlineData("bad host:9100")]
    [InlineData("tcp://host:9100")]
    [InlineData("host/path")]
    public void TryParse_BadAddress_IsRejected(string? address)
        => Assert.False(PrinterEndpoint.TryParse(address, out _));

    [Theory]
    [InlineData("Development", "", true)]
    [InlineData("Development", "host:0", false)]
    [InlineData("Production", "", false)]
    [InlineData("Staging", "", false)]
    [InlineData("Production", "host", true)]
    public void Validator_AllowsNoPrinterInDevelopmentOnly(string environment, string address, bool valid)
    {
        var result = Validator(environment).Validate(null, new PrinterOptions { Address = address });

        Assert.Equal(valid, result.Succeeded);
    }

    // The configuration value "3" binds as 3 days.
    [Theory]
    [InlineData(-1)]
    [InlineData(3 * 24 * 60 * 60)]
    public void Validator_ConnectTimeoutOutOfRange_Fails(int seconds)
    {
        var result = Validator("Production")
            .Validate(null, new PrinterOptions { ConnectTimeout = TimeSpan.FromSeconds(seconds) });

        Assert.True(result.Failed);
        Assert.Contains("Printer:ConnectTimeout", result.FailureMessage);
    }
}
