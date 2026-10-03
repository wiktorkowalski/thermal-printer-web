using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalPrinterWeb.Controllers;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;
using BarcodeType = ThermalPrinterWeb.Models.BarcodeType;

namespace ThermalPrinterWeb.Tests;

// No test here reaches the network: the document is built before the first printer call.
public sealed class PayloadErrorTests
{
    private const string Secret = "SECRET-CALLER-CONTENT";

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception) + exception));
    }

    // The production handler registration, so a new block type is covered here too.
    private static PrinterService NewService(ILogger<PrinterService>? logger = null) => new(
        logger ?? NullLogger<PrinterService>.Instance,
        new ServiceCollection().AddLogging().AddPrinterBlockHandlers().BuildServiceProvider().GetServices<IBlockHandler>());

    private static PrintContent Text(string content = "ok") => new() { Type = ContentType.Text, Content = content };

    private static PrintContent BadBarcode() => new()
    {
        Type = ContentType.Barcode,
        Content = Secret,
        BarcodeOptions = new BarcodeOptions { Type = BarcodeType.EAN13 }
    };

    private static PrintContent Barcode(int? heightInDots, BarLabelPosition labelPosition = BarLabelPosition.Below, string content = "BOX-0007") => new()
    {
        Type = ContentType.Barcode,
        Content = content,
        BarcodeOptions = new BarcodeOptions { HeightInDots = heightInDots, LabelPosition = labelPosition }
    };

    private static PrintContent LineFeed(int lines) => new() { Type = ContentType.LineFeed, Lines = lines };

    internal static string OverPaper(int block, ContentType type)
        => $"Block {block} ({type}): the document is over the limit of {PaperLength.MaxDots} dots of paper ({PaperLength.MaxDots / PaperLength.DotsPerMetre} m)";

    public static TheoryData<PrintContent, string> InvalidBlocks() => new()
    {
        { new PrintContent { Type = ContentType.Separator, SeparatorChar = "" }, "Block 1 (Separator): separatorChar must not be empty" },
        { new PrintContent { Type = ContentType.Separator, SeparatorLength = -1 }, "Block 1 (Separator): separatorLength must not be negative" },
        { new PrintContent { Type = ContentType.Image, Content = "not base64 !!" }, "Block 1 (Image): Image rejected: not valid base64 (13 characters)." },
        { new PrintContent { Type = ContentType.Image, Content = Convert.ToBase64String("not an image"u8) }, "Block 1 (Image): Image rejected: format not supported (format unknown, 12 bytes). Send a PNG or JPEG." },
        { BadBarcode(), "Block 1 (Barcode): content is not a valid EAN13 barcode" },
        { new PrintContent { Type = ContentType.Separator, SeparatorLength = 65 }, "Block 1 (Separator): separatorLength 65 is over the limit of 64" },
        { new PrintContent { Type = ContentType.Separator, SeparatorLength = int.MaxValue }, "Block 1 (Separator): separatorLength 2147483647 is over the limit of 64" },
        { new PrintContent { Type = ContentType.LineFeed, Lines = 101 }, "Block 1 (LineFeed): lines 101 is over the limit of 100" },
        { Text(new string('x', 10_001)), "Block 1 (Text): text length 10001 is over the limit of 10000" },
        { Text(new string('\n', 500)), "Block 1 (Text): text line count 501 is over the limit of 500" },
        // The encoder turns each of these into LF.
        { Text(new string('\r', 500)), "Block 1 (Text): text line count 501 is over the limit of 500" },
        { Text(new string('\f', 500)), "Block 1 (Text): text line count 501 is over the limit of 500" },
        { Text(new string('\u2028', 500)), "Block 1 (Text): text line count 501 is over the limit of 500" },
        { new PrintContent { Type = ContentType.Image, Content = "AAAA", ImageOptions = new ImageOptions { MaxWidth = 0 } }, "Block 1 (Image): imageOptions.maxWidth 0 must be at least 1" },
        { new PrintContent { Type = ContentType.Barcode, Content = "Zażółć" }, "Block 1 (Barcode): a CODE128 barcode holds printable ASCII only" },
        { Barcode(0), "Block 1 (Barcode): barcodeOptions.heightInDots 0 is outside the range 1 to 255" },
        { Barcode(-1), "Block 1 (Barcode): barcodeOptions.heightInDots -1 is outside the range 1 to 255" },
        { Barcode(256), "Block 1 (Barcode): barcodeOptions.heightInDots 256 is outside the range 1 to 255" },
        // A barcode block with no content prints nothing; the height is still checked.
        { Barcode(256, content: ""), "Block 1 (Barcode): barcodeOptions.heightInDots 256 is outside the range 1 to 255" },
        { Barcode(int.MinValue), "Block 1 (Barcode): barcodeOptions.heightInDots -2147483648 is outside the range 1 to 255" },
        { new PrintContent { Type = ContentType.QRCode, Content = new string('ż', 1477) }, "Block 1 (QRCode): content is 2954 bytes as UTF-8; a Model2 QR code holds at most 2953" }
    };

    [Fact]
    public async Task BuildDocumentAsync_BlocksAtTheLimits_AreAccepted()
    {
        List<PrintContent> content =
        [
            new() { Type = ContentType.Separator, SeparatorLength = 64 },
            new() { Type = ContentType.LineFeed, Lines = 100 },
            Text(new string('x', 10_000)),
            Text(new string('\n', 499)),
            // No paper: the document stays under the paper limit.
            .. Enumerable.Range(0, PrinterService.MaxBlocks - 4).Select(_ => new PrintContent { Type = ContentType.CodePage, Content = "PC852" })
        ];
        Assert.Equal(PrinterService.MaxBlocks, content.Count);

        Assert.NotEmpty(await NewService().BuildDocumentAsync(content, null));
    }

    [Fact]
    public async Task PrintAsync_TooManyBlocks_IsAValidationFailureLoggedOnce()
    {
        var logger = new RecordingLogger<PrinterService>();
        var content = Enumerable.Range(0, PrinterService.MaxBlocks + 1).Select(_ => Text(Secret)).ToList();

        var result = await NewService(logger).PrintAsync(content);

        Assert.Equal(PrintResult.Invalid("block count 501 is over the limit of 500"), result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain(Secret, entry.Message);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public async Task PrintAsync_ImageBlocks_AreLimitedPerDocument(int count, bool accepted)
    {
        var logger = new RecordingLogger<PrinterService>();
        // Not an image: an accepted document fails later, at block 0.
        var content = Enumerable.Range(0, count).Select(_ => new PrintContent { Type = ContentType.Image, Content = "AAAA" }).ToList();
        // Image blocks with no picture do not count.
        content.Add(new PrintContent { Type = ContentType.Image });

        var result = await NewService(logger).PrintAsync(content);

        Assert.Equal(PrintFailure.Validation, result.Failure);
        Assert.Equal(accepted, result.Error!.StartsWith("Block 0 (Image):", StringComparison.Ordinal));
        if (!accepted)
            Assert.Equal("image block count 21 is over the limit of 20", result.Error);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information).Level);
    }

    [Fact]
    public async Task PrintAsync_TooMuchPrinterData_IsAValidationFailureLoggedOnce()
    {
        var logger = new RecordingLogger<PrinterService>();
        var content = Enumerable.Range(0, 250).Select(_ => Text()).ToList();

        // Text and images pass the paper limit first, so the handler here adds bytes and no paper.
        var result = await new PrinterService(logger, [new BulkHandler()]).PrintAsync(content);

        // 2 MiB / 10,003 bytes per block = 209 blocks fit.
        Assert.Equal(PrintResult.Invalid("Block 209: the document is over the limit of 2097152 bytes of printer data"), result);
        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    private sealed class BulkHandler : IBlockHandler
    {
        public ContentType Type => ContentType.Text;

        public Task HandleAsync(PrintContent item, BlockContext ctx)
        {
            ctx.Add(new byte[10_000]);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public async Task BuildDocumentAsync_BarcodeHeightAtTheLimits_GoesToThePrinter(int height)
    {
        var bytes = await NewService().BuildDocumentAsync([Barcode(height)], null);

        // GS h n
        Assert.Contains(bytes, command => command.AsSpan().SequenceEqual([(byte)0x1D, (byte)0x68, (byte)height]));
    }

    [Fact]
    public async Task BuildDocumentAsync_BarcodeWithoutHeight_KeepsThePrinterDefault()
    {
        var bytes = await NewService().BuildDocumentAsync([Barcode(null)], null);

        Assert.DoesNotContain(bytes, command => command.AsSpan().StartsWith([(byte)0x1D, (byte)0x68]));
    }

    [Theory]
    [InlineData(-1, null, "options.defaultLineSpacing -1 is outside the range 0 to 255")]
    [InlineData(256, null, "options.defaultLineSpacing 256 is outside the range 0 to 255")]
    [InlineData(-70000, null, "options.defaultLineSpacing -70000 is outside the range 0 to 255")]
    [InlineData(null, -1, "options.feedLinesAfterPrint -1 is outside the range 0 to 255")]
    [InlineData(null, 256, "options.feedLinesAfterPrint 256 is outside the range 0 to 255")]
    [InlineData(null, int.MinValue, "options.feedLinesAfterPrint -2147483648 is outside the range 0 to 255")]
    public async Task PrintAsync_OptionOutsideThePrinterRange_IsAValidationFailureLoggedOnce(int? lineSpacing, int? feed, string expectedError)
    {
        var logger = new RecordingLogger<PrinterService>();
        var options = new PrintOptions { DefaultLineSpacing = lineSpacing, FeedLinesAfterPrint = feed ?? 3 };

        var result = await NewService(logger).PrintAsync([Text()], options);

        Assert.Equal(PrintResult.Invalid(expectedError), result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Rejected print: " + expectedError, entry.Message);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(255, 255)]
    public async Task BuildDocumentAsync_OptionsAtTheLimits_GoToThePrinter(int lineSpacing, int feed)
    {
        var options = new PrintOptions { DefaultLineSpacing = lineSpacing, FeedLinesAfterPrint = feed };

        var bytes = await NewService().BuildDocumentAsync([Text()], options);

        // ESC 3 n, then GS V 65 n and ESC 2 at the end: the spacing must not stay for the next job.
        Assert.Contains(bytes, command => command.AsSpan().SequenceEqual([(byte)0x1B, (byte)0x33, (byte)lineSpacing]));
        Assert.Equal([0x1D, 0x56, 0x41, (byte)feed], bytes[^2]);
        Assert.Equal([0x1B, 0x32], bytes[^1]);
    }

    [Fact]
    public async Task BuildDocumentAsync_NoLineSpacing_SendsNoSpacingCommand()
    {
        var bytes = await NewService().BuildDocumentAsync([Text()], new PrintOptions());

        Assert.DoesNotContain(bytes, command => command.AsSpan().StartsWith([(byte)0x1B, (byte)0x33]));
        Assert.DoesNotContain(bytes, command => command.AsSpan().SequenceEqual([(byte)0x1B, (byte)0x32]));
    }

    public static TheoryData<List<PrintContent>, PrintOptions?, string?> PaperJobs()
    {
        var doubleHeight = new PrintContent { Type = ContentType.Text, Content = new string('\n', 499), Style = [PrintStyle.DoubleHeight] };
        var widestQRCode = new PrintContent
        {
            Type = ContentType.QRCode,
            Content = new string('x', 2953),
            QRCodeOptions = new QRCodeOptions { Size = QRCodeSize.ExtraLarge }
        };
        var cut = new PrintContent { Type = ContentType.Cut };

        return new()
        {
            // One line is 29 dots: 11 x 2900 + 3 x 29 = 31,987.
            { [.. Enumerable.Repeat(LineFeed(100), 11), LineFeed(3)], null, null },
            { [.. Enumerable.Repeat(LineFeed(100), 11), LineFeed(4)], null, OverPaper(11, ContentType.LineFeed) },
            // The job from the issue: 500 blocks of 100 lines.
            { [.. Enumerable.Repeat(LineFeed(100), 500)], null, OverPaper(11, ContentType.LineFeed) },
            // 500 DoubleHeight lines are 26,500 dots.
            { [doubleHeight], null, null },
            { [doubleHeight, doubleHeight], null, OverPaper(1, ContentType.Text) },
            // A long line wraps: 10,000 characters are 417 DoubleWidth lines.
            { [.. Enumerable.Repeat(new PrintContent { Type = ContentType.Text, Content = new string('x', 10_000), Style = [PrintStyle.DoubleWidth] }, 3)], null, OverPaper(2, ContentType.Text) },
            // Line spacing 255: 125 lines are 31,875 dots.
            { [Text(new string('\n', 124))], new PrintOptions { DefaultLineSpacing = 255 }, null },
            { [Text(new string('\n', 125))], new PrintOptions { DefaultLineSpacing = 255 }, OverPaper(0, ContentType.Text) },
            // Each cut feeds 255 dots and the 124 dots to the cutter: 379.
            { [.. Enumerable.Repeat(cut, 84)], new PrintOptions { FeedLinesAfterPrint = 255 }, null },
            { [.. Enumerable.Repeat(cut, 85)], new PrintOptions { FeedLinesAfterPrint = 255 }, OverPaper(84, ContentType.Cut) },
            // A feed of 0 still moves the paper to the cutter.
            { [.. Enumerable.Repeat(cut, 259)], new PrintOptions { FeedLinesAfterPrint = 0 }, OverPaper(258, ContentType.Cut) },
            // The printer wraps on bytes. KATAKANA has no .NET encoding, so the text goes out as UTF-8: 3 bytes for one euro sign.
            { [.. Enumerable.Repeat(Text(new string('€', 10_000)), 2)], new PrintOptions { CodePage = "KATAKANA" }, OverPaper(1, ContentType.Text) },
            // PC852 has no ellipsis: it prints as three dots.
            { [.. Enumerable.Repeat(Text(new string('…', 10_000)), 2)], null, OverPaper(1, ContentType.Text) },
            // 177 modules x 6 dots + one line = 1091 dots.
            { [.. Enumerable.Repeat(widestQRCode, 29)], null, null },
            { [.. Enumerable.Repeat(widestQRCode, 30)], null, OverPaper(29, ContentType.QRCode) },
            // 255 dots + one line = 284 dots.
            { [.. Enumerable.Repeat(Barcode(255), 112)], null, null },
            { [.. Enumerable.Repeat(Barcode(255), 113)], null, OverPaper(112, ContentType.Barcode) },
            // A caption above and below: 255 dots + two lines = 313 dots.
            { [.. Enumerable.Repeat(Barcode(255, BarLabelPosition.Both), 102)], null, null },
            { [.. Enumerable.Repeat(Barcode(255, BarLabelPosition.Both), 103)], null, OverPaper(102, ContentType.Barcode) }
        };
    }

    [Theory]
    [MemberData(nameof(PaperJobs))]
    public async Task BuildDocumentAsync_PaperLength_IsLimitedPerDocument(List<PrintContent> content, PrintOptions? options, string? expectedError)
    {
        var logger = new RecordingLogger<PrinterService>();

        // Not PrintAsync: an accepted document would go to the printer.
        var build = NewService(logger).BuildDocumentAsync(content, options);

        if (expectedError is null)
        {
            Assert.NotEmpty(await build);
            return;
        }

        Assert.Equal(expectedError, (await Assert.ThrowsAsync<PrintContentException>(() => build)).Message);
        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Theory]
    [MemberData(nameof(InvalidBlocks))]
    public async Task PrintAsync_InvalidBlock_IsAValidationFailure(PrintContent block, string expectedError)
    {
        var result = await NewService().PrintAsync([Text(), block]);

        Assert.False(result.Success);
        Assert.Equal(PrintFailure.Validation, result.Failure);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public async Task PrintAsync_InvalidBlock_KeepsCallerContentOutOfErrorAndLog()
    {
        var logger = new RecordingLogger<PrinterService>();

        var result = await NewService(logger).PrintAsync([BadBarcode()]);

        Assert.DoesNotContain(Secret, result.Error);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(Secret, e.Message));
        // A caller's bad payload is logged once, and not as an error.
        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("block 0 (Barcode)", entry.Message);
    }

    [Fact]
    public async Task PrintAsync_NullBlock_IsAValidationFailure()
    {
        var result = await NewService().PrintAsync([Text(), null!]);

        Assert.Equal(PrintResult.Invalid("Block 1: must not be null"), result);
    }

    // The name is caller text: it reaches the log cleaned and cut.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildDocumentAsync_UnknownCodePage_LogsACleanName(bool asBlock)
    {
        var hostile = "nope\r\nFAKE LOG LINE\u001b[31m" + new string('x', 10_240);
        var logger = new RecordingLogger<PrinterService>();
        var blockLogger = new RecordingLogger<CodePageBlockHandler>();
        var service = new PrinterService(logger, [new TextBlockHandler(), new CodePageBlockHandler(blockLogger)]);

        if (asBlock)
            await service.BuildDocumentAsync([new PrintContent { Type = ContentType.CodePage, Content = hostile }, Text()], null);
        else
            await service.BuildDocumentAsync([Text()], new PrintOptions { CodePage = hostile });

        var entry = Assert.Single(asBlock ? blockLogger.Entries : logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.StartsWith("Unknown code page \"nope??FAKE LOG LINE?[31mxxxxxxxx\"", entry.Message);
        Assert.DoesNotContain('\n', entry.Message);
        Assert.True(entry.Message.Length < 100, entry.Message);
    }

    private sealed class FakePrinterService(PrintResult result) : IPrinterService
    {
        public Task<PrintResult> PrintAsync(List<PrintContent> content, PrintOptions? options = null) => Task.FromResult(result);
        public Task<PrinterStatus> GetStatusAsync() => throw new NotSupportedException();
        public Task<bool> BeepAsync(int count, int duration) => throw new NotSupportedException();
    }

    private static async Task<ObjectResult> PrintViaControllerAsync(PrintResult result)
    {
        var controller = new PrinterController(new FakePrinterService(result), new PrintJobLog(NullLogger<PrintJobLog>.Instance, new HttpContextAccessor()));
        return Assert.IsAssignableFrom<ObjectResult>(await controller.Print(new PrintRequest { Content = [Text()] }));
    }

    [Fact]
    public async Task Print_ValidationFailure_Returns400()
    {
        var response = await PrintViaControllerAsync(PrintResult.Invalid("bad block"));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(new PrintResponse(false, "bad block", "validation"), response.Value);
    }

    [Fact]
    public async Task Print_PrinterFault_Returns503()
    {
        var response = await PrintViaControllerAsync(PrintResult.PrinterFault("Printer not ready: cover open"));

        Assert.Equal(503, response.StatusCode);
        Assert.Equal(new PrintResponse(false, "Printer not ready: cover open", "printer"), response.Value);
    }

    [Fact]
    public async Task Print_Success_Returns200()
    {
        var response = await PrintViaControllerAsync(PrintResult.Ok);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(new PrintResponse(true), response.Value);
    }
}

// Real PrinterService behind the HTTP pipeline: every request fails before the first printer call.
public sealed class PayloadErrorHttpTests(PayloadErrorHttpTests.ProductionApp app) : IClassFixture<PayloadErrorHttpTests.ProductionApp>
{
    public sealed class ProductionApp : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Production");
    }

    private readonly HttpClient _client = app.CreateClient();

    private async Task<PrintResponse> PostBadRequestAsync(string json)
    {
        var response = await _client.PostAsync("/api/printer", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PrintResponse>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.Equal("validation", body.Type);
        return body;
    }

    [Fact]
    public async Task PostPrinter_EmptySeparatorChar_Returns400WithReason()
    {
        var body = await PostBadRequestAsync("""{"content":[{"type":"Separator","separatorChar":""}]}""");

        Assert.Equal("Block 0 (Separator): separatorChar must not be empty", body.Error);
    }

    [Theory]
    [InlineData("""{"content":[{"type":"Bogus"}]}""", "$.content[0].type")]
    [InlineData("""{"content":"text"}""", "$.content")]
    [InlineData("""{"content":[""", "$.content")]
    public async Task PostPrinter_ModelBindingError_ReturnsPrintResponseShape(string json, string expectedInError)
    {
        var body = await PostBadRequestAsync(json);

        Assert.StartsWith(expectedInError, body.Error);
    }

    [Fact]
    public async Task PostBeep_BadQueryValue_ReturnsPrintResponseShape()
    {
        var response = await _client.PostAsync("/api/printer/beep?count=abc", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PrintResponse>();
        Assert.Equal(new PrintResponse(false, "count: The value 'abc' is not valid.", "validation"), body);
    }

    [Fact]
    public async Task PostPrinter_EmptyRequest_Returns400()
    {
        var body = await PostBadRequestAsync("{}");

        Assert.Equal("Request must have Content array or both Name and Message", body.Error);
    }

    [Theory]
    [InlineData("""{"content":[{"type":"Barcode","content":"BOX-0007","barcodeOptions":{"heightInDots":256}}]}""", "Block 0 (Barcode): barcodeOptions.heightInDots 256 is outside the range 1 to 255")]
    [InlineData("""{"content":[{"type":"Text","content":"x"}],"options":{"feedLinesAfterPrint":-1}}""", "options.feedLinesAfterPrint -1 is outside the range 0 to 255")]
    [InlineData("""{"content":[{"type":"Text","content":"x"}],"options":{"defaultLineSpacing":256}}""", "options.defaultLineSpacing 256 is outside the range 0 to 255")]
    public async Task PostPrinter_NumberOutsideThePrinterRange_Returns400WithFieldAndRange(string json, string expectedError)
    {
        var body = await PostBadRequestAsync(json);

        Assert.Equal(expectedError, body.Error);
    }

    [Fact]
    public async Task PostPrinter_JobOverThePaperLimit_Returns400()
    {
        var blocks = string.Join(',', Enumerable.Repeat("""{"type":"LineFeed","lines":100}""", 500));

        var body = await PostBadRequestAsync("{\"content\":[" + blocks + "]}");

        Assert.Equal(PayloadErrorTests.OverPaper(11, ContentType.LineFeed), body.Error);
    }
}
