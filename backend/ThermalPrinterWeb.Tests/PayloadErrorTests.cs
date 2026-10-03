using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalPrinterWeb.Controllers;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;
using BarcodeType = ThermalPrinterWeb.Models.BarcodeType;

namespace ThermalPrinterWeb.Tests;

// A payload the printer can never print is the caller's fault: 400 "validation".
// 503 "printer" stays for the printer and the connection. None of these tests
// reach the network: the document is built before the first printer call.
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

    private static PrinterService NewService(ILogger<PrinterService>? logger = null) => new(
        logger ?? NullLogger<PrinterService>.Instance,
        [
            new TextBlockHandler(),
            new ImageBlockHandler(NullLogger<ImageBlockHandler>.Instance),
            new BarcodeBlockHandler(),
            new QRCodeBlockHandler(),
            new LineFeedBlockHandler(),
            new CutBlockHandler(),
            new SeparatorBlockHandler(),
            new CodePageBlockHandler(NullLogger<CodePageBlockHandler>.Instance)
        ]);

    private static PrintContent Text(string content = "ok") => new() { Type = ContentType.Text, Content = content };

    private static PrintContent BadBarcode() => new()
    {
        Type = ContentType.Barcode,
        Content = Secret,
        BarcodeOptions = new BarcodeOptions { Type = BarcodeType.EAN13 }
    };

    public static TheoryData<PrintContent, string> InvalidBlocks() => new()
    {
        { new PrintContent { Type = ContentType.Separator, SeparatorChar = "" }, "Block 1 (Separator): separatorChar must not be empty" },
        { new PrintContent { Type = ContentType.Separator, SeparatorLength = -1 }, "Block 1 (Separator): separatorLength must not be negative" },
        { new PrintContent { Type = ContentType.Image, Content = "not base64 !!" }, "Block 1 (Image): content is not valid for this block type" },
        { new PrintContent { Type = ContentType.Image, Content = Convert.ToBase64String("not an image"u8) }, "Block 1 (Image): content is not valid for this block type" },
        { BadBarcode(), "Block 1 (Barcode): content is not a valid EAN13 barcode" }
    };

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
    public async Task Separator_EmptyChar_Throws()
    {
        var ctx = new BlockContext(new ESCPOS_NET.Emitters.EPSON(), null);

        var ex = await Assert.ThrowsAsync<PrintContentException>(() => new SeparatorBlockHandler()
            .HandleAsync(new PrintContent { Type = ContentType.Separator, SeparatorChar = "" }, ctx));

        Assert.Equal("separatorChar must not be empty", ex.Message);
    }

    private sealed class FakePrinterService(PrintResult result) : IPrinterService
    {
        public Task<PrintResult> PrintAsync(List<PrintContent> content, PrintOptions? options = null) => Task.FromResult(result);
        public Task<PrinterStatus> GetStatusAsync() => throw new NotSupportedException();
        public Task<bool> BeepAsync(int count, int duration) => throw new NotSupportedException();
    }

    private static async Task<ObjectResult> PrintViaControllerAsync(PrintResult result)
    {
        var controller = new PrinterController(NullLogger<PrinterController>.Instance, new FakePrinterService(result));
        return Assert.IsAssignableFrom<ObjectResult>(await controller.Print(new PrintRequest { Content = [Text()] }));
    }

    [Fact]
    public async Task Controller_ValidationFailure_Returns400()
    {
        var response = await PrintViaControllerAsync(PrintResult.Invalid("bad block"));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(new PrintResponse(false, "bad block", "validation"), response.Value);
    }

    [Fact]
    public async Task Controller_PrinterFault_Returns503()
    {
        var response = await PrintViaControllerAsync(PrintResult.PrinterFault("Printer not ready: cover open"));

        Assert.Equal(503, response.StatusCode);
        Assert.Equal(new PrintResponse(false, "Printer not ready: cover open", "printer"), response.Value);
    }

    [Fact]
    public async Task Controller_Success_Returns200()
    {
        var response = await PrintViaControllerAsync(new PrintResult(true));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(new PrintResponse(true), response.Value);
    }
}

// The whole HTTP pipeline with the real PrinterService. Every request here fails
// before the first printer call, so nothing reaches the network.
public sealed class PayloadErrorHttpTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.WithWebHostBuilder(b => b.UseEnvironment("Production")).CreateClient();

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
    public async Task EmptySeparatorChar_Returns400WithReason()
    {
        var body = await PostBadRequestAsync("""{"content":[{"type":"Separator","separatorChar":""}]}""");

        Assert.Equal("Block 0 (Separator): separatorChar must not be empty", body.Error);
    }

    [Fact]
    public async Task BadBase64Image_Returns400()
    {
        var body = await PostBadRequestAsync("""{"content":[{"type":"Image","content":"not base64 !!"}]}""");

        Assert.StartsWith("Block 0 (Image):", body.Error);
    }

    [Theory]
    [InlineData("""{"content":[{"type":"Bogus"}]}""", "$.content[0].type")]
    [InlineData("""{"content":"text"}""", "$.content")]
    [InlineData("""{"content":[""", "")]
    public async Task ModelBindingError_ReturnsPrintResponseShape(string json, string expectedInError)
    {
        var body = await PostBadRequestAsync(json);

        Assert.False(string.IsNullOrEmpty(body.Error));
        Assert.Contains(expectedInError, body.Error);
    }

    [Fact]
    public async Task EmptyRequest_Returns400()
    {
        var body = await PostBadRequestAsync("{}");

        Assert.Equal("Request must have Content array or both Name and Message", body.Error);
    }
}
