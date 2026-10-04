using System.Net.Sockets;
using System.Text;
using ESCPOS_NET;
using ESCPOS_NET.Emitters;
using ESCPOS_NET.Utilities;
using Microsoft.Extensions.Options;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Journal;
using ThermalPrinterWeb.Services.Printing;

namespace ThermalPrinterWeb.Services;

internal sealed class PrinterService(
    ILogger<PrinterService> logger,
    IEnumerable<IBlockHandler> handlers,
    IOptions<PrinterOptions> printerOptions) : IPrinterService
{
    private readonly IReadOnlyDictionary<ContentType, IBlockHandler> _handlers = handlers.ToDictionary(h => h.Type);

    // Null: no printer is configured, and no call opens a connection.
    private readonly PrinterEndpoint? _endpoint = printerOptions.Value.ResolveEndpoint();
    private readonly TimeSpan _connectTimeout = printerOptions.Value.ConnectTimeout;

    // What the status read answers with no printer: ready, so the print path runs to its end.
    private static readonly PrinterStatus NoPrinterStatus = new(true, true, false, false, false, "no printer");

    // The only text a caller gets for these faults. The exception holds the printer
    // address and socket details: it stays in the server log.
    internal const string UnreachableError = "Printer unreachable";
    internal const string InternalError = "Print failed: internal error";

    private static readonly TimeSpan StatusReadTimeout = TimeSpan.FromSeconds(2);

    // The printer renders single-byte code pages only; raw UTF-8 prints as garbage
    // for anything outside ASCII, so default to Latin-2 (covers Polish) instead.
    private const string DefaultCodePage = CodePages.DefaultName;

    // The Kestrel default, set in Program.cs because the image size limit in ImageBlockHandler depends on it.
    internal const long MaxRequestBodyBytes = 30_000_000;

    // A long receipt is about 100 blocks.
    internal const int MaxBlocks = 500;

    // One image at the pixel limit takes about 1 s of CPU inside the decode queue, whatever its output size.
    internal const int MaxImageBlocks = 20;

    // Printer data for one job. A full-width image 4096 dots tall is 295 KB; text is 1 byte per character.
    internal const int MaxOutputBytes = 2 * 1024 * 1024;

    // ESC 3 n and GS V m n take one byte each.
    internal const int MaxLineSpacing = 255;
    internal const int MaxFeedBeforeCut = 255;

    // ESC B n t (1B 42): buzzer - n beeps each of length t. Both clamp to 1..9.
    private const int BuzzerMin = 1;
    private const int BuzzerMax = 9;

    public async Task<PrintResult> PrintAsync(List<PrintContent> content, PrintOptions? options = null)
    {
        // Null outside a journaled request. The journal stores what this method learns about the job.
        var trace = PrintJobTrace.Current;
        try
        {
            // Build first: it needs no printer, so a bad payload is reported as such
            // even while the printer is off.
            var byteContent = await BuildDocumentAsync(content, options);
            var job = ByteSplicer.Combine(byteContent.ToArray());
            trace?.Bytes = job;

            // Fire-and-forget writes buffer even when the printer can't print (cover open,
            // paper out), so a job would falsely report success. Refuse instead of lying.
            var status = await GetStatusAsync();
            trace?.Status = status;
            if (!status.Ready)
            {
                // An unreachable printer is logged in GetStatusAsync, with the exception.
                if (status.Reachable)
                {
                    logger.LogWarning(
                        "Refusing print: printer not ready ({Reason}); status={Raw}",
                        status.NotReadyReason, status.Raw);
                }

                return PrintResult.PrinterFault($"Printer not ready: {status.NotReadyReason}");
            }

            if (_endpoint is not { } endpoint)
            {
                logger.LogInformation("No printer configured: print job of {ByteCount} bytes not sent", job.Length);
                return PrintResult.Ok;
            }

            logger.LogDebug("Connecting to printer at {Address}", endpoint);
            var printer = new ImmediateNetworkPrinter(new ImmediateNetworkPrinterSettings
            {
                ConnectionString = endpoint.ToString(),
                PrinterName = "ThermalPrinter"
            });

            try
            {
                await printer.WriteAsync(job);
            }
            catch (Exception ex)
            {
                // The status read passed, then the connection for the job failed: the job is lost.
                logger.LogError(ex, "Print failed: no connection to the printer at {Address}", endpoint);
                trace?.Exception = ex;
                return PrintResult.PrinterFault(UnreachableError);
            }

            return PrintResult.Ok;
        }
        catch (PrintContentException ex)
        {
            // Logged where the block failed.
            return PrintResult.Invalid(ex.Message);
        }
        catch (PrintBusyException ex)
        {
            logger.LogWarning("Rejected print: server busy, {Reason}", ex.Message);
            return PrintResult.Busy;
        }
        catch (Exception ex)
        {
            // Not a printer fault and not the payload: a fault in this service.
            logger.LogError(ex, "Print failed");
            trace?.Exception = ex;
            return PrintResult.PrinterFault(InternalError);
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
            logger.LogWarning("Rejected print: the document has {BlockCount} blocks, the limit is {MaxBlocks}", content.Count, MaxBlocks);
            throw PrintContentException.OverLimit("block count", content.Count, MaxBlocks);
        }

        var imageBlocks = content.Count(block => block is { Type: ContentType.Image, Content.Length: > 0 });
        if (imageBlocks > MaxImageBlocks)
        {
            logger.LogWarning("Rejected print: the document has {ImageBlockCount} image blocks, the limit is {MaxImageBlocks}", imageBlocks, MaxImageBlocks);
            throw PrintContentException.OverLimit("image block count", imageBlocks, MaxImageBlocks);
        }

        if (options is not null)
        {
            CheckOptionRange("options.defaultLineSpacing", options.DefaultLineSpacing, MaxLineSpacing);
            CheckOptionRange("options.feedLinesAfterPrint", options.FeedLinesAfterPrint, MaxFeedBeforeCut);
        }

        var e = new EPSON();
        var ctx = new BlockContext(e, options);

        // ESC @ first: the job starts from the power-on state, whatever the job before left
        // (line spacing, text size, barcode and QR settings). It also resets the code page,
        // so the code page command comes next, then the options.
        ctx.Add(e.Initialize());

        // Determine text encoding based on code page
        var codePageName = options?.CodePage ?? DefaultCodePage;
        var codePage = CodePages.Resolve(codePageName);
        if (codePage.HasValue)
        {
            ctx.Add(e.CodePage(codePage.Value));
            ctx.Encoding = CodePages.GetEncoding(codePageName);
            logger.LogDebug("Using code page {CodePage}", codePage.Value);
        }
        else
        {
            // After ESC @ the printer has its own default code page, not the one of the job before.
            logger.LogWarning(
                "Unknown code page \"{CodePage}\", printing raw UTF-8 bytes",
                LogSafeText.Clean(codePageName, CodePages.MaxLoggedNameLength));
        }

        if (options?.DefaultLineSpacing != null)
            ctx.Add(e.SetLineSpacingInDots(options.DefaultLineSpacing.Value));

        var outputBytes = 0L;
        var counted = 0;
        foreach (var (index, item) in content.Index())
        {
            await AddBlockAsync(index, item, ctx);

            for (; counted < ctx.Output.Count; counted++)
                outputBytes += ctx.Output[counted].Length;
            if (outputBytes > MaxOutputBytes)
            {
                logger.LogWarning(
                    "Rejected print: the document passes {MaxOutputBytes} bytes of printer data at block {BlockIndex} of {BlockCount}",
                    MaxOutputBytes, index, content.Count);
                throw new PrintContentException($"Block {index}: the document is over the limit of {MaxOutputBytes} bytes of printer data");
            }
        }

        // Count only: the content is caller input and the endpoint is public.
        if (ctx.ReplacedCharacters > 0)
            logger.LogInformation("Replaced {Count} unprintable character(s) with '?'", ctx.ReplacedCharacters);

        if (options?.AutoCut != false && !ctx.HasCut)
        {
            // Not counted as paper: one cut, under 5 cm.
            var feedLines = options?.FeedLinesAfterPrint ?? 3;
            ctx.Add(e.FullCutAfterFeed(feedLines));
        }

        PrintJobTrace.Current?.PaperDots = ctx.PaperDots;
        return ctx.Output;
    }

    private void CheckOptionRange(string field, int? value, int max)
    {
        if (value is not { } number || (number >= 0 && number <= max))
            return;

        logger.LogWarning("Rejected print: {Field} {Value} is outside the range {Min} to {Max}", field, number, 0, max);
        throw PrintContentException.OutOfRange(field, number, 0, max);
    }

    private async Task AddBlockAsync(int index, PrintContent? item, BlockContext ctx)
    {
        // JSON "content": [null] binds to a null entry.
        if (item is null)
        {
            logger.LogWarning("Rejected print: block {BlockIndex} is null", index);
            throw new PrintContentException($"Block {index}: must not be null");
        }

        // JSON "type": 99 binds: the enum converter takes any number. A skipped block would report a print that did not happen.
        if (!_handlers.TryGetValue(item.Type, out var handler))
        {
            logger.LogWarning("Rejected print: block {BlockIndex} has the unsupported type {Type}", index, item.Type);
            throw new PrintContentException($"Block {index} ({item.Type}): type is not supported");
        }

        try
        {
            // First: after it, every enum value of the block has a name.
            BlockEnums.Check(item);

            var e = ctx.Emitter;
            ctx.Add(item.Alignment switch
            {
                Alignment.Left => e.LeftAlign(),
                Alignment.Right => e.RightAlign(),
                _ => e.CenterAlign()
            });

            await handler.HandleAsync(item, ctx);
        }
        // Busy is server load, not the payload: it must not turn into a 400.
        catch (Exception ex) when (ex is not PrintBusyException)
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
        // No log line: the UI polls the status.
        if (_endpoint is not { } endpoint)
            return NoPrinterStatus;

        try
        {
            using var client = new TcpClient();
            using var connectCts = new CancellationTokenSource(_connectTimeout);
            await client.ConnectAsync(endpoint.Host, endpoint.Port, connectCts.Token);
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
            logger.LogWarning(ex, "Failed to read printer status from {Address}", endpoint);
            // No exception text: the status goes to the caller as it is.
            return new PrinterStatus(false, false, false, false, false);
        }
    }

    public async Task<bool> BeepAsync(int count, int duration)
    {
        var n = Math.Clamp(count, BuzzerMin, BuzzerMax);
        var t = Math.Clamp(duration, BuzzerMin, BuzzerMax);
        byte[] command = [0x1B, 0x42, (byte)n, (byte)t]; // ESC B n t
        if (_endpoint is not { } endpoint)
        {
            logger.LogInformation("No printer configured: buzzer command of {ByteCount} bytes not sent", command.Length);
            return true;
        }

        try
        {
            using var client = new TcpClient();
            using var connectCts = new CancellationTokenSource(_connectTimeout);
            await client.ConnectAsync(endpoint.Host, endpoint.Port, connectCts.Token);
            using var stream = client.GetStream();
            await stream.WriteAsync(command);
            logger.LogInformation("Buzzer beeped {Count} time(s), duration {Duration}", n, t);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Buzzer beep failed ({Address})", endpoint);
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
}
