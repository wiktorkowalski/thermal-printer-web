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

    // A long receipt is about 100 blocks.
    internal const int MaxBlocks = 500;

    // ESC B n t (1B 42): buzzer - n beeps each of length t. Both clamp to 1..9.
    private const int BuzzerMin = 1;
    private const int BuzzerMax = 9;

    public async Task<PrintResult> PrintAsync(List<PrintContent> content, PrintOptions? options = null)
    {
        try
        {
            // Build first: it needs no printer, so a bad payload is reported as such
            // even while the printer is off.
            var byteContent = await BuildDocumentAsync(content, options);

            // Fire-and-forget writes buffer even when the printer can't print (cover open,
            // paper out), so a job would falsely report success. Refuse instead of lying.
            var status = await GetStatusAsync();
            if (!status.Ready)
            {
                logger.LogWarning(
                    "Refusing print: printer not ready ({Reason}); status={Raw}",
                    status.NotReadyReason, status.Raw);
                return PrintResult.PrinterFault($"Printer not ready: {status.NotReadyReason}");
            }

            logger.LogInformation("Connecting to printer at {Address}", PrinterAddress);
            var printer = new ImmediateNetworkPrinter(new ImmediateNetworkPrinterSettings
            {
                ConnectionString = PrinterAddress,
                PrinterName = "ThermalPrinter"
            });

            await printer.WriteAsync(ByteSplicer.Combine(byteContent.ToArray()));
            logger.LogInformation("Printing complete");
            return PrintResult.Ok;
        }
        catch (PrintContentException ex)
        {
            // Logged where the block failed.
            return PrintResult.Invalid(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Print failed");
            return PrintResult.PrinterFault(ex.Message);
        }
    }

    // Turns a content list into the raw ESC/POS byte stream. Pure (no I/O) so it
    // can be exercised without the printer attached. Dispatch is a handler
    // registry keyed by ContentType - add a block type by registering a handler,
    // no switch to edit.
    internal async Task<List<byte[]>> BuildDocumentAsync(List<PrintContent> content, PrintOptions? options)
    {
        if (content.Count > MaxBlocks)
        {
            logger.LogWarning("Rejected print: {BlockCount} blocks, limit {MaxBlocks}", content.Count, MaxBlocks);
            throw new PrintContentException($"Document has {content.Count} blocks, over the limit of {MaxBlocks}");
        }

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

        foreach (var (index, item) in content.Index())
            await AddBlockAsync(index, item, ctx);

        // Count only: the content is caller input and the endpoint is public.
        if (ctx.ReplacedCharacters > 0)
            logger.LogInformation("Replaced {Count} unprintable character(s) with '?'", ctx.ReplacedCharacters);

        if (options?.AutoCut != false && !ctx.HasCut)
        {
            var feedLines = options?.FeedLinesAfterPrint ?? 3;
            ctx.Add(e.FullCutAfterFeed(feedLines));
        }

        return ctx.Output;
    }

    private async Task AddBlockAsync(int index, PrintContent? item, BlockContext ctx)
    {
        // JSON "content": [null] binds to a null entry.
        if (item is null)
        {
            logger.LogWarning("Rejected print: block {BlockIndex} is null", index);
            throw new PrintContentException($"Block {index}: must not be null");
        }

        var e = ctx.Emitter;
        ctx.Add(item.Alignment switch
        {
            Alignment.Left => e.LeftAlign(),
            Alignment.Right => e.RightAlign(),
            _ => e.CenterAlign()
        });

        if (!_handlers.TryGetValue(item.Type, out var handler))
        {
            logger.LogWarning("Unsupported content type: {Type}", item.Type);
            return;
        }

        try
        {
            await handler.HandleAsync(item, ctx);
        }
        catch (Exception ex)
        {
            // Handlers do no printer I/O, so a throw here means the block cannot be
            // printed as sent. Library messages repeat caller content: keep them out
            // of the log and the response, the endpoint is public.
            var reason = ex is PrintContentException ? ex.Message : "content is not valid for this block type";
            logger.LogWarning(
                "Rejected print: block {BlockIndex} ({Type}) failed with {ExceptionType}: {Reason}",
                index, item.Type, ex.GetType().Name, reason);
            throw new PrintContentException($"Block {index} ({item.Type}): {reason}");
        }
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
