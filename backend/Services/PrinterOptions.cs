using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;

namespace ThermalPrinterWeb.Services;

internal sealed class PrinterOptions
{
    public const string SectionName = "Printer";

    // The printer on the home network. Also the value in appsettings.json: the code
    // default keeps production on the printer if that file is replaced.
    public const string DefaultAddress = "192.168.123.100:9100";

    // "host" or "host:port". Empty: no printer, the service sends nothing (Development only).
    public string Address { get; set; } = DefaultAddress;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public bool HasPrinter => !string.IsNullOrWhiteSpace(Address);

    // Null: no printer. PrinterOptionsValidator rejects a bad address at startup, with the setting name.
    public PrinterEndpoint? ResolveEndpoint()
    {
        if (!HasPrinter)
            return null;

        return PrinterEndpoint.TryParse(Address, out var endpoint)
            ? endpoint
            : throw new FormatException("The printer address is not valid");
    }
}

internal readonly record struct PrinterEndpoint(string Host, int Port)
{
    public const int DefaultPort = 9100;

    // IPv6 is out: ESCPOS_NET splits its connection string at each colon.
    public static bool TryParse(string? address, out PrinterEndpoint endpoint)
    {
        endpoint = default;
        var parts = (address ?? string.Empty).Trim().Split(':');
        if (parts.Length > 2)
            return false;

        var host = parts[0];
        if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
            return false;

        var port = DefaultPort;
        // Digits only: no sign, no space.
        if (parts.Length == 2
            && !(int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= IPEndPoint.MaxPort))
            return false;

        endpoint = new PrinterEndpoint(host, port);
        return true;
    }

    public override string ToString() => $"{Host}:{Port}";
}

internal sealed class PrinterOptionsValidator(IHostEnvironment environment) : IValidateOptions<PrinterOptions>
{
    private static readonly TimeSpan MaxConnectTimeout = TimeSpan.FromMinutes(1);

    public ValidateOptionsResult Validate(string? name, PrinterOptions options)
    {
        // "3" binds as 3 days.
        if (options.ConnectTimeout < TimeSpan.Zero || options.ConnectTimeout > MaxConnectTimeout)
        {
            return ValidateOptionsResult.Fail(
                $"Printer:ConnectTimeout {options.ConnectTimeout} is outside the range 00:00:00 to {MaxConnectTimeout}. Use the form hh:mm:ss.");
        }

        if (!options.HasPrinter)
        {
            return environment.IsDevelopment()
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    $"Printer:Address is empty. Only the Development environment runs with no printer; this is {environment.EnvironmentName}. "
                    + "Set Printer:Address (environment variable Printer__Address) to host or host:port.");
        }

        return PrinterEndpoint.TryParse(options.Address, out _)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Printer:Address \"{options.Address}\" is not valid. Use host or host:port, with a port from 1 to 65535.");
    }
}
