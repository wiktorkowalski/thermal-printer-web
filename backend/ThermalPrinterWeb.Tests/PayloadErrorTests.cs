using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalPrinterWeb.Controllers;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;
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
    public async Task PrintAsync_NullBlock_IsAValidationFailure()
    {
        var result = await NewService().PrintAsync([Text(), null!]);

        Assert.Equal(PrintResult.Invalid("Block 1: must not be null"), result);
    }

    // Out-of-range options must not fail the build: outside a block, a throw is
    // reported as a printer fault.
    [Fact]
    public async Task BuildDocumentAsync_ExtremeOptions_DoNotThrow()
    {
        var options = new PrintOptions { CodePage = "nope", DefaultLineSpacing = -70000, FeedLinesAfterPrint = int.MinValue };

        var bytes = await NewService().BuildDocumentAsync([Text()], options);

        Assert.NotEmpty(bytes);
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
}
