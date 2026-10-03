using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;

namespace ThermalPrinterWeb.Tests;

// Real JSON-RPC over /mcp, with the printer replaced: no test reaches the network.
public sealed class McpToolTests(McpToolTests.McpApp app) : IClassFixture<McpToolTests.McpApp>
{
    private const string Secret = "SECRET-CALLER-CONTENT";
    private const int FontAColumns = 48;
    private const int DoubleWidthColumns = 24;

    public sealed class RecordingPrinter : IPrinterService
    {
        public ConcurrentQueue<(List<PrintContent> Content, PrintOptions? Options)> Jobs { get; } = [];
        public PrintResult Result { get; set; } = PrintResult.Ok;

        public Task<PrintResult> PrintAsync(List<PrintContent> content, PrintOptions? options = null)
        {
            Jobs.Enqueue((content, options));
            return Task.FromResult(Result);
        }

        public Task<PrinterStatus> GetStatusAsync() => throw new NotSupportedException();
        public Task<bool> BeepAsync(int count, int duration) => Task.FromResult(true);
    }

    public sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((logLevel, category, formatter(state, exception) + exception));
        }
    }

    public sealed class McpApp : WebApplicationFactory<Program>
    {
        public RecordingPrinter Printer { get; } = new();
        public RecordingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPrinterService>();
                services.AddSingleton<IPrinterService>(Printer);
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
            });
        }
    }

    private readonly HttpClient _client = app.CreateClient();

    private async Task<JsonElement> RpcAsync(string method, string? paramsJson = null)
    {
        var body = $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}"{{(paramsJson is null ? "" : $",\"params\":{paramsJson}")}}}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();

        // Streamable HTTP answers as one SSE event.
        var data = text.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        return JsonDocument.Parse(data).RootElement.GetProperty("result");
    }

    private async Task<(bool IsError, string Text)> CallAsync(string tool, string? argumentsJson)
    {
        var arguments = argumentsJson is null ? "" : $",\"arguments\":{argumentsJson}";
        var result = await RpcAsync("tools/call", $$"""{"name":"{{tool}}"{{arguments}}}""");

        var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
        return (isError, result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    private async Task<JsonElement> ToolAsync(string name)
    {
        var tools = (await RpcAsync("tools/list")).GetProperty("tools");
        return tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == name);
    }

    private static string ExampleIn(JsonElement tool)
    {
        var description = tool.GetProperty("description").GetString()!;
        var marker = description.LastIndexOf("Example: ", StringComparison.Ordinal);
        Assert.True(marker >= 0, "The tool description holds no example.");
        return description[(marker + "Example: ".Length)..];
    }

    private static void AssertEveryPropertyDescribed(JsonElement schema, string path)
    {
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            Assert.True(
                property.Value.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()),
                $"{path}.{property.Name} has no description");
        }
    }

    [Fact]
    public async Task Initialize_AnyClient_SendsServerInstructions()
    {
        var result = await RpcAsync("initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}""");

        var instructions = result.GetProperty("instructions").GetString();
        Assert.Contains("48 characters", instructions);
        Assert.Contains("print_note", instructions);
        Assert.Contains("""{"content":[{"type":"Text","content":"..."}]}""", instructions);
    }

    [Fact]
    public async Task ToolsList_AfterTheChange_KeepsTheToolNames()
    {
        var tools = (await RpcAsync("tools/list")).GetProperty("tools");

        Assert.Equal(
            ["beep", "get_status", "print", "print_note"],
            tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).Order());
    }

    [Theory]
    [InlineData("print", "content,options,source")]
    [InlineData("print_note", "title,message,imageBase64,source")]
    [InlineData("beep", "count,duration")]
    public async Task ToolsList_EveryTool_HasOnlyOptionalDescribedArguments(string tool, string expectedArguments)
    {
        var schema = (await ToolAsync(tool)).GetProperty("inputSchema");

        Assert.Equal(expectedArguments.Split(','), schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.True(
            !schema.TryGetProperty("required", out var required) || required.GetArrayLength() == 0,
            $"{tool} still has required arguments");
        AssertEveryPropertyDescribed(schema, tool);
    }

    [Fact]
    public async Task ToolsList_PrintTool_DescribesEveryNestedProperty()
    {
        var tool = await ToolAsync("print");
        var properties = tool.GetProperty("inputSchema").GetProperty("properties");
        var block = properties.GetProperty("content").GetProperty("items");

        AssertEveryPropertyDescribed(block, "content[]");
        AssertEveryPropertyDescribed(properties.GetProperty("options"), "options");
        Assert.All(
            ["qrCodeOptions", "barcodeOptions", "imageOptions"],
            options => AssertEveryPropertyDescribed(block.GetProperty("properties").GetProperty(options), options));

        // The numbers a caller needs to avoid a mid-word wrap.
        var content = block.GetProperty("properties").GetProperty("content").GetProperty("description").GetString();
        Assert.Contains("48 characters per line", content);
        // Code content is rejected, never changed: a caller must not expect the '?' that text gets.
        Assert.Contains("any other control character (tab and a CR with no \\n included) rejects the block", content);
        Assert.Contains("printable ASCII only; any other character rejects the block", content);
        Assert.DoesNotContain("same control character rules", content);
        Assert.Contains("A control character in QRCode or Barcode content rejects the document", tool.GetProperty("description").GetString());
        Assert.Contains("2953", block.GetProperty("properties").GetProperty("qrCodeOptions").GetProperty("properties").GetProperty("model").GetProperty("description").GetString());
    }

    // The numbers in the descriptions are typed by hand: an attribute cannot read the constants.
    [Fact]
    public async Task ToolsList_PrintTool_StatesTheLimitsTheCodeApplies()
    {
        var tool = await ToolAsync("print");
        var block = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("content").GetProperty("items").GetProperty("properties");
        string Description(string property) => block.GetProperty(property).GetProperty("description").GetString()!;
        var imageOptions = block.GetProperty("imageOptions").GetProperty("properties");

        Assert.Contains(
            $"at most {PrinterService.MaxBlocks} blocks, {PrinterService.MaxImageBlocks} of them images, and prints at most {PaperLength.MaxDots / PaperLength.DotsPerMetre} m of paper",
            tool.GetProperty("description").GetString());
        var options = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("options").GetProperty("properties");
        Assert.Contains($"0 to {PrinterService.MaxLineSpacing}.", options.GetProperty("defaultLineSpacing").GetProperty("description").GetString());
        Assert.Contains($"0 to {PrinterService.MaxFeedBeforeCut}.", options.GetProperty("feedLinesAfterPrint").GetProperty("description").GetString());
        Assert.Contains(
            $"{BarcodeBlockHandler.MinHeightInDots} to {BarcodeBlockHandler.MaxHeightInDots}.",
            block.GetProperty("barcodeOptions").GetProperty("properties").GetProperty("heightInDots").GetProperty("description").GetString());
        Assert.Contains($"at most {TextBlockHandler.MaxLength} characters and {TextBlockHandler.MaxLines} lines", Description("content"));
        Assert.Contains($"at most {ImageBlockHandler.MaxImageBytes / (1024 * 1024)} MB, {ImageBlockHandler.MaxSidePixels} pixels per side and {ImageBlockHandler.MaxPixels / 1_000_000} megapixels", Description("content"));
        Assert.Contains($"at most {LineFeedBlockHandler.MaxLines}.", Description("lines"));
        Assert.Contains($"at most {SeparatorBlockHandler.MaxLength}.", Description("separatorLength"));
        Assert.Contains($"1 to {ImageBlockHandler.MaxPrintHeight}.", imageOptions.GetProperty("maxHeight").GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("print")]
    [InlineData("print_note")]
    [InlineData("beep")]
    public async Task ToolsCall_DescriptionExample_IsAValidCall(string tool)
    {
        var example = ExampleIn(await ToolAsync(tool));

        var (isError, text) = await CallAsync(tool, example);

        Assert.False(isError, text);
        Assert.DoesNotContain("Not printed", text);
    }

    // The examples follow the advice they give: no line is wider than its style allows.
    [Fact]
    public async Task ToolsCall_PrintExample_KeepsEveryLineInsideThePaper()
    {
        app.Printer.Jobs.Clear();

        await CallAsync("print", PrinterTools.PrintExample);

        Assert.True(app.Printer.Jobs.TryDequeue(out var job));
        foreach (var block in job.Content.Where(block => block.Type == ContentType.Text))
        {
            var width = block.Style?.Contains(PrintStyle.DoubleWidth) == true ? DoubleWidthColumns : FontAColumns;
            Assert.All(block.Content!.Split('\n'), line => Assert.True(line.Length <= width, line));
        }
    }

    [Theory]
    // Wrong names: the caller guessed before it read the schema (#37).
    [InlineData("print", $$$"""{"text":"{{{Secret}}}"}""", "'content' is missing")]
    [InlineData("print", $$$"""{"title":"{{{Secret}}}","message":"m"}""", "'content' is missing")]
    [InlineData("print", "{}", "'content' is missing")]
    [InlineData("print", null, "'content' is missing")]
    [InlineData("print", """{"content":null}""", "'content' is missing")]
    [InlineData("print_note", $$$"""{"content":[{"type":"Text","content":"{{{Secret}}}"}]}""", "'title' and 'message' are missing")]
    [InlineData("print_note", $$$"""{"text":"{{{Secret}}}"}""", "'title' and 'message' are missing")]
    [InlineData("print_note", $$$"""{"title":"{{{Secret}}}"}""", "'message' is missing")]
    [InlineData("print_note", $$$"""{"message":"{{{Secret}}}"}""", "'title' is missing")]
    [InlineData("print_note", """{"title":null,"message":null}""", "'title' and 'message' are missing")]
    // Wrong JSON types: these fail in the SDK, before the tool body.
    // The message names the argument: the SDK path starts at the argument (#67).
    [InlineData("print", $$$"""{"content":"{{{Secret}}}"}""", "'content' has the wrong JSON type")]
    [InlineData("print", $$$"""{"content":{"type":"Text","content":"{{{Secret}}}"}}""", "'content' has the wrong JSON type")]
    [InlineData("print", $$$"""{"content":[{"type":"{{{Secret}}}"}]}""", "the value at content[0].type has the wrong JSON type or is not a known name")]
    [InlineData("print", $$$"""{"content":[{"type":"Text","content":"x"},{"type":"Text","lines":"{{{Secret}}}"}]}""", "the value at content[1].lines has the wrong JSON type or is not a known name")]
    [InlineData("print", $$$"""{"content":[{"type":"Text","content":"x","style":"{{{Secret}}}"}]}""", "the value at content[0].style has the wrong JSON type or is not a known name")]
    [InlineData("print", $$$"""{"content":[{"type":"Text","content":"x"}],"options":"{{{Secret}}}"}""", "'options' has the wrong JSON type")]
    [InlineData("print", $$$"""{"content":[{"type":"Text","content":"x"}],"options":{"defaultLineSpacing":"{{{Secret}}}"}}""", "the value at options.defaultLineSpacing has the wrong JSON type or is not a known name")]
    [InlineData("print_note", $$$"""{"title":5,"message":"{{{Secret}}}"}""", "'title' has the wrong JSON type")]
    [InlineData("beep", $$$"""{"count":"{{{Secret}}}"}""", "'count' has the wrong JSON type")]
    [InlineData("beep", $$$"""{"{{{Secret}}}":1,"duration":true}""", "'duration' has the wrong JSON type")]
    [InlineData("beep", """{"count":1.5}""", "'count' has the wrong JSON type")]
    // The SDK reads "5" as 5: the valid argument is not named.
    [InlineData("beep", """{"count":"5","duration":1.5}""", "'duration' has the wrong JSON type")]
    // Two arguments do not fit, or the value is over the int range: no argument is named.
    [InlineData("beep", $$$"""{"count":1.5,"duration":"{{{Secret}}}"}""", "an argument has the wrong JSON type")]
    [InlineData("beep", """{"count":99999999999}""", "an argument has the wrong JSON type")]
    public async Task ToolsCall_WrongShape_AnswersWithTheCorrectShape(string tool, string? arguments, string expectedProblem)
    {
        app.Printer.Jobs.Clear();
        app.Logs.Entries.Clear();

        var (isError, text) = await CallAsync(tool, arguments);

        Assert.True(isError);
        Assert.StartsWith($"Wrong arguments for '{tool}': {expectedProblem}. Example of a valid call: {{", text);
        Assert.Contains(PrinterTools.ValidCallFor(tool)!, text);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("ThermalPrinterWeb.", text);
        Assert.DoesNotContain("System.", text);
        Assert.Empty(app.Printer.Jobs);

        // The caller's mistake: logged once at Information, never as a warning or an error.
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
        var rejection = Assert.Single(app.Logs.Entries, entry => entry.Category.StartsWith("ThermalPrinterWeb.Mcp", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, rejection.Level);
        Assert.Equal($"Rejected MCP call to {tool}: {expectedProblem}", rejection.Message);
    }

    [Fact]
    public async Task ToolsCall_PrintWrongShape_PointsToPrintNote()
    {
        var (_, text) = await CallAsync("print", """{"text":"hello"}""");

        Assert.EndsWith($"For a plain note use print_note: {PrinterTools.PrintNoteExample}", text);
    }

    [Fact]
    public async Task ToolsCall_PrintNoteWithOnlyTitleAndMessage_Prints()
    {
        app.Printer.Jobs.Clear();

        var (isError, text) = await CallAsync("print_note", """{"title":"T","message":"M"}""");

        Assert.False(isError);
        Assert.Equal("Printed.", text);
        Assert.True(app.Printer.Jobs.TryDequeue(out var job));
        Assert.Equal(["T", "M"], job.Content.Where(block => block.Type == ContentType.Text).Select(block => block.Content));
        Assert.DoesNotContain(job.Content, block => block.Type == ContentType.Image);
    }

    [Fact]
    public async Task ToolsCall_PrintWithOnlyContent_Prints()
    {
        app.Printer.Jobs.Clear();

        var (isError, text) = await CallAsync("print", """{"content":[{"type":"Text","content":"hello"}]}""");

        Assert.False(isError);
        Assert.Equal("Printed.", text);
        Assert.True(app.Printer.Jobs.TryDequeue(out var job));
        Assert.Equal("hello", Assert.Single(job.Content).Content);
        Assert.Null(job.Options);
    }

    [Fact]
    public async Task ToolsCall_BeepWithNoArguments_Beeps()
    {
        var (isError, text) = await CallAsync("beep", null);

        Assert.False(isError);
        Assert.Equal("Beeped 1x.", text);
    }

    // ArgumentShapeFilter depends on this: a number in a string is not the faulty argument.
    [Fact]
    public async Task ToolsCall_BeepWithCountAsString_Beeps()
    {
        var (isError, text) = await CallAsync("beep", """{"count":"5"}""");

        Assert.False(isError);
        Assert.Equal("Beeped 5x.", text);
    }

    // A rejected payload keeps the text and the shape it had before: not an MCP error.
    [Fact]
    public async Task ToolsCall_PrinterRejectsTheJob_KeepsTheReasonText()
    {
        app.Printer.Result = PrintResult.Invalid("Block 0 (Separator): separatorChar must not be empty");
        try
        {
            var (isError, text) = await CallAsync("print", """{"content":[{"type":"Separator","separatorChar":""}]}""");

            Assert.False(isError);
            Assert.Equal("Not printed: Block 0 (Separator): separatorChar must not be empty", text);
        }
        finally
        {
            app.Printer.Result = PrintResult.Ok;
        }
    }

    // MCP has no status code: the text says that the server is busy and that the same call can be sent again.
    [Theory]
    [InlineData("print", """{"content":[{"type":"Text","content":"hello"}]}""")]
    [InlineData("print_note", """{"title":"T","message":"M"}""")]
    public async Task ToolsCall_ServerBusy_SaysToSendTheJobAgain(string tool, string arguments)
    {
        app.Printer.Result = PrintResult.Busy;
        try
        {
            var (isError, text) = await CallAsync(tool, arguments);

            Assert.False(isError);
            Assert.Equal($"Not printed: {PrintResult.Busy.Error}", text);
            Assert.StartsWith("Not printed: Server busy", text);
        }
        finally
        {
            app.Printer.Result = PrintResult.Ok;
        }
    }
}
