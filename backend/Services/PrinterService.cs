using System.Net.Sockets;
using System.Text;
using ESCPOS_NET;
using ESCPOS_NET.Emitters;
using ESCPOS_NET.Utilities;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing;

namespace ThermalPrinterWeb.Services;

internal sealed class PrinterService(ILogger<PrinterService> logger, IEnumerable<IBlockHandler> handlers) : IPrinterService
{
    private readonly IReadOnlyDictionary<ContentType, IBlockHandler> _handlers = handlers.ToDictionary(h => h.Type);
    private const string PrinterAddress = "192.168.123.100:9100";
    private const int DefaultPrinterPort = 9100;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StatusReadTimeout = TimeSpan.FromSeconds(2);

    // The printer renders single-byte code pages only; raw UTF-8 prints as garbage
    // for anything outside ASCII, so default to Latin-2 (covers Polish) instead.
    private const string DefaultCodePage = "PC852";

    // ESC B n t (1B 42): buzzer - n beeps each of length t. Both clamp to 1..9.
    private const int BuzzerMin = 1;
    private const int BuzzerMax = 9;

    public async Task<PrintResult> PrintAsync(List<PrintContent> content, PrintOptions? options = null)
    {
        try
        {
            // Fire-and-forget writes buffer even when the printer can't print (cover open,
            // paper out), so a job would falsely report success. Refuse instead of lying.
            var status = await GetStatusAsync();
            if (!status.Ready)
            {
                logger.LogWarning(
                    "Refusing print: printer not ready ({Reason}); status={Raw}",
                    status.NotReadyReason, status.Raw);
                return new PrintResult(false, $"Printer not ready: {status.NotReadyReason}");
            }

            logger.LogInformation("Connecting to printer at {Address}", PrinterAddress);
            var printer = new ImmediateNetworkPrinter(new ImmediateNetworkPrinterSettings
            {
                ConnectionString = PrinterAddress,
                PrinterName = "ThermalPrinter"
            });

            var byteContent = await BuildDocumentAsync(content, options);

            await printer.WriteAsync(ByteSplicer.Combine(byteContent.ToArray()));
            logger.LogInformation("Printing complete");
            return new PrintResult(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Print failed");
            return new PrintResult(false, ex.Message);
        }
    }

    // Turns a content list into the raw ESC/POS byte stream. Pure (no I/O) so it
    // can be exercised without the printer attached. Dispatch is a handler
    // registry keyed by ContentType - add a block type by registering a handler,
    // no switch to edit.
    private async Task<List<byte[]>> BuildDocumentAsync(List<PrintContent> content, PrintOptions? options)
    {
        var e = new EPSON();
        var ctx = new BlockContext(e, options);

        // Determine text encoding based on code page
        var codePageName = options?.CodePage ?? DefaultCodePage;
        var codePage = CodePages.Resolve(codePageName);
        if (codePage.HasValue)
        {
            ctx.Add(e.CodePage(codePage.Value));
            ctx.Encoding = CodePages.GetEncoding(codePageName);
            logger.LogDebug("Using code page {CodePage}", codePageName);
        }
        else
        {
            logger.LogWarning("Unknown code page {CodePage}, printing raw UTF-8 bytes", codePageName);
        }

        if (options?.DefaultLineSpacing != null)
            ctx.Add(e.SetLineSpacingInDots(options.DefaultLineSpacing.Value));

        foreach (var item in content)
        {
            ctx.Add(item.Alignment switch
            {
                Alignment.Left => e.LeftAlign(),
                Alignment.Right => e.RightAlign(),
                _ => e.CenterAlign()
            });

            if (_handlers.TryGetValue(item.Type, out var handler))
                await handler.HandleAsync(item, ctx);
            else
                logger.LogWarning("Unsupported content type: {Type}", item.Type);
        }

        if (options?.AutoCut != false && !ctx.HasCut)
        {
            var feedLines = options?.FeedLinesAfterPrint ?? 3;
            ctx.Add(e.FullCutAfterFeed(feedLines));
        }

        return ctx.Output;
    }

    public async Task<PrinterStatus> GetStatusAsync()
    {
        var (host, port) = ParseAddress(PrinterAddress);
        try
        {
            using var client = new TcpClient();
            using var connectCts = new CancellationTokenSource(ConnectTimeout);
            await client.ConnectAsync(host, port, connectCts.Token);
            using var stream = client.GetStream();

            // ESC/POS real-time status (DLE EOT n) — each query returns one byte.
            var online = true;
            var coverOpen = false;
            var paperOut = false;
            var paperLow = false;
            var raw = new StringBuilder();

            var printerStatus = await QueryStatusByteAsync(stream, [0x10, 0x04, 0x01]);
            if (printerStatus.HasValue)
            {
                online = (printerStatus.Value & 0x08) == 0; // bit 3 set => offline
                raw.Append($"n1={printerStatus.Value:x2} ");
            }

            var offlineStatus = await QueryStatusByteAsync(stream, [0x10, 0x04, 0x02]);
            if (offlineStatus.HasValue)
            {
                coverOpen = (offlineStatus.Value & 0x04) != 0; // bit 2 set => cover open
                raw.Append($"n2={offlineStatus.Value:x2} ");
            }

            var paperStatus = await QueryStatusByteAsync(stream, [0x10, 0x04, 0x04]);
            if (paperStatus.HasValue)
            {
                paperOut = (paperStatus.Value & 0x60) == 0x60; // bits 5,6 set => paper end
                paperLow = (paperStatus.Value & 0x0C) == 0x0C; // bits 2,3 set => paper near-end
                raw.Append($"n4={paperStatus.Value:x2}");
            }

            var status = new PrinterStatus(true, online, coverOpen, paperOut, paperLow, raw.ToString().Trim());
            logger.LogDebug("Printer status: {Status}", status);
            return status;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read printer status from {Address}", PrinterAddress);
            return new PrinterStatus(false, false, false, false, false, ex.Message);
        }
    }

    public async Task<bool> BeepAsync(int count, int duration)
    {
        var n = Math.Clamp(count, BuzzerMin, BuzzerMax);
        var t = Math.Clamp(duration, BuzzerMin, BuzzerMax);
        var (host, port) = ParseAddress(PrinterAddress);
        try
        {
            using var client = new TcpClient();
            using var connectCts = new CancellationTokenSource(ConnectTimeout);
            await client.ConnectAsync(host, port, connectCts.Token);
            using var stream = client.GetStream();
            byte[] command = [0x1B, 0x42, (byte)n, (byte)t]; // ESC B n t
            await stream.WriteAsync(command);
            logger.LogInformation("Buzzer beeped {Count} time(s), duration {Duration}", n, t);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Buzzer beep failed ({Address})", PrinterAddress);
            return false;
        }
    }

    // Sends one real-time status query and reads its single-byte reply, or null on timeout.
    private static async Task<byte?> QueryStatusByteAsync(NetworkStream stream, byte[] query)
    {
        await stream.WriteAsync(query);
        using var cts = new CancellationTokenSource(StatusReadTimeout);
        var buffer = new byte[1];
        try
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cts.Token);
            return read == 1 ? buffer[0] : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static (string Host, int Port) ParseAddress(string address)
    {
        var parts = address.Split(':');
        return parts.Length > 1 && int.TryParse(parts[1], out var port)
            ? (parts[0], port)
            : (parts[0], DefaultPrinterPort);
    }
}
