using System.Net;
using System.Text.Json;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Tests;

// Issue #44: fields with no effect on this printer are gone from the caller texts.
// Callers and stored jobs still hold them: each one binds as before and the job has the bytes from before.
public sealed class OldFieldTests(FakePrinterApp app) : IClassFixture<FakePrinterApp>
{
    private readonly HttpClient _client = app.CreateClient();

    // A job with every field of the issue: Italic, partialCut, highDensity (on a block that does not read it),
    // a code page that prints wrong glyphs, feedLinesAfterPrint.
    private const string OldContent =
        """
        [{"type":"Text","content":"Zażółć","alignment":"Left","style":["Italic","Bold"],"imageOptions":{"useLegacyMode":true,"highDensity":false}},
         {"type":"Barcode","content":"0123456789012","barcodeOptions":{"type":"GS1_DATABAR_OMNIDIRECTIONAL"}},
         {"type":"Cut","partialCut":true}]
        """;

    private const string OldOptions = """{"codePage":"WPC1250","autoCut":false,"feedLinesAfterPrint":5}""";

    private const string OldJob = $$"""{"content":{{OldContent}},"options":{{OldOptions}}}""";

    // The Blocks and Options columns of that job, as the server stored them before the change: every model property, defaults included.
    private const string StoredBlocks =
        """
        [{"type":"Text","content":"Zażółć","alignment":"Left","style":["Italic","Bold"],"size":null,"barcodeOptions":null,"qrCodeOptions":null,
          "imageOptions":{"maxWidth":576,"maxHeight":576,"preserveAspectRatio":true,"useLegacyMode":true,"highDensity":false},
          "lines":1,"partialCut":false,"separatorChar":"=","separatorLength":32,"signalOptions":null},
         {"type":"Barcode","content":"0123456789012","alignment":"Center","style":null,"size":null,
          "barcodeOptions":{"type":"GS1_DATABAR_OMNIDIRECTIONAL","heightInDots":100,"width":"Default","labelPosition":"Below"},"qrCodeOptions":null,
          "imageOptions":null,"lines":1,"partialCut":false,"separatorChar":"=","separatorLength":32,"signalOptions":null},
         {"type":"Cut","content":null,"alignment":"Center","style":null,"size":null,"barcodeOptions":null,"qrCodeOptions":null,
          "imageOptions":null,"lines":1,"partialCut":true,"separatorChar":"=","separatorLength":32,"signalOptions":null}]
        """;

    private const string StoredOptions = """{"codePage":"WPC1250","defaultLineSpacing":null,"autoCut":false,"feedLinesAfterPrint":5}""";

    // ESC @, then ESC t 45: the page that ESCPOS_NET sends for WPC1250.
    private static readonly byte[] Wpc1250Prelude = [0x1B, 0x40, 0x1B, 0x74, 45];

    // ESC ! n with the Bold bit (8) and the Italic bit (64).
    private static readonly byte[] BoldItalic = [0x1B, 0x21, 0x48];

    private static readonly byte[] FullDensity = [0x30, 0x31, 0x33, 0x33];
    private static readonly byte[] HalfDensity = [0x30, 0x31, 0x32, 0x32];

    // "Zażółć" and LF.
    private static readonly byte[] TextInWindows1250 = [(byte)'Z', (byte)'a', 0xBF, 0xF3, 0xB3, 0xE6, 0x0A];

    // GS V 66 5: the partial cut command with the feed of the job.
    private static readonly byte[] PartialCutAfterFive = [0x1D, 0x56, 0x42, 5];

    private static void AssertOldBytes(byte[] job)
    {
        Assert.Equal(Wpc1250Prelude, job[..Wpc1250Prelude.Length]);
        Assert.Equal(1, job.AsSpan().Count(BoldItalic));
        // The text in Windows-1250, not in PC852.
        Assert.Equal(1, job.AsSpan().Count(TextInWindows1250));
        Assert.Equal(PartialCutAfterFive, job[^PartialCutAfterFive.Length..]);
    }

    [Fact]
    public async Task PostPrinter_JobWithEveryOldField_SendsTheBytesFromBefore()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);

        var (status, body) = await wired.CreateClient().SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, OldJob);

        Assert.True(status == HttpStatusCode.OK, body);
        AssertOldBytes(await printer.NextJobAsync());
    }

    // The same job over HTTP and from a row of before the change: the row reads, reprints and sends the same bytes.
    [Fact]
    public async Task GetJobAndReprint_RowStoredBeforeTheChange_ServesTheOldFieldsAndSendsTheSameBytes()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var client = wired.CreateClient();
        await client.SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, OldJob);
        var fresh = await printer.NextJobAsync();
        var id = Guid.CreateVersion7();
        await wired.JournalIdleAsync();
        await using (var db = wired.JournalDb())
        {
            db.PrintJobs.Add(new PrintJob
            {
                Id = id,
                CreatedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                Transport = "http",
                Result = JobResult.Printed,
                HttpStatus = 200,
                AppVersion = "before-44",
                BlockCount = 3,
                Payload = new PrintJobPayload { JobId = id, Headers = "{}", Blocks = StoredBlocks, Options = StoredOptions }
            });
            await db.SaveChangesAsync();
        }

        var (readStatus, readBody) = await client.SendJsonAsync(HttpMethod.Get, $"{TestHttp.PrintUrl}/jobs/{id}");
        var (reprintStatus, reprintBody) = await client.SendJsonAsync(HttpMethod.Post, $"{TestHttp.PrintUrl}/jobs/{id}/reprint");
        var reprinted = await printer.NextJobAsync();

        Assert.True(readStatus == HttpStatusCode.OK, readBody);
        var read = JsonDocument.Parse(readBody).RootElement;
        var blocks = read.GetProperty("blocks");
        Assert.Equal("Italic", blocks[0].GetProperty("style")[0].GetString());
        Assert.False(blocks[0].GetProperty("imageOptions").GetProperty("highDensity").GetBoolean());
        Assert.Equal("GS1_DATABAR_OMNIDIRECTIONAL", blocks[1].GetProperty("barcodeOptions").GetProperty("type").GetString());
        Assert.True(blocks[2].GetProperty("partialCut").GetBoolean());
        Assert.Equal("WPC1250", read.GetProperty("options").GetProperty("codePage").GetString());
        Assert.Equal(5, read.GetProperty("options").GetProperty("feedLinesAfterPrint").GetInt32());

        Assert.True(reprintStatus == HttpStatusCode.OK, reprintBody);
        AssertOldBytes(reprinted);
        Assert.Equal(fresh, reprinted);
    }

    // highDensity on an Image block, where the handler reads it: legacy mode sends the same bytes for both values,
    // the other mode sends the density of the value. So the field stays bound, and the MCP text says when it is read.
    [Fact]
    public async Task PostPrinter_ImageWithHighDensity_ChangesTheBytesOnlyOutsideLegacyMode()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var client = wired.CreateClient();
        var picture = TestImages.PngBase64();

        async Task<byte[]> JobAsync(string imageOptions)
        {
            var json = $$"""{"content":[{"type":"Image","content":"{{picture}}","imageOptions":{{imageOptions}}}]}""";
            var (status, body) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, json);
            Assert.True(status == HttpStatusCode.OK, body);
            return await printer.NextJobAsync();
        }

        var legacy = await JobAsync("""{"highDensity":true}""");
        var legacyLow = await JobAsync("""{"highDensity":false}""");
        var modern = await JobAsync("""{"useLegacyMode":false,"highDensity":true}""");
        var modernLow = await JobAsync("""{"useLegacyMode":false,"highDensity":false}""");

        Assert.Equal(legacy, await JobAsync("{}"));
        Assert.Equal(legacy, legacyLow);
        Assert.NotEqual(legacy, modern);
        Assert.NotEqual(modern, modernLow);
        // GS ( L, function 49: the density of the picture, 51 for full and 50 for half.
        Assert.Equal(1, modern.AsSpan().Count(FullDensity));
        Assert.Equal(1, modernLow.AsSpan().Count(HalfDensity));
    }

    // The binder skips a property that it does not know: a field that leaves the models later is no new 400.
    [Fact]
    public async Task PostPrinter_PropertiesThatNoModelHas_PrintsTheSameBytes()
    {
        await using var printer = new WirePrinter();
        await using var wired = new LoopbackPrinterApp(printer.Port);
        var client = wired.CreateClient();
        const string withUnknown =
            """{"content":[{"type":"Text","content":"x","noSuchField":true,"imageOptions":{"noSuchOption":[1,2]}}],"options":{"noSuchSetting":{"a":1}},"noSuchPart":"y"}""";
        const string plain = """{"content":[{"type":"Text","content":"x","imageOptions":{}}],"options":{}}""";

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, withUnknown);
        var first = await printer.NextJobAsync();
        await client.SendJsonAsync(HttpMethod.Post, TestHttp.PrintUrl, plain);
        var second = await printer.NextJobAsync();

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(second, first);
    }

    [Fact]
    public async Task McpPrint_OldAndUnknownFields_BindsTheOldOnesAndPrints()
    {
        app.Printer.Jobs.Clear();
        // The old job, with one more property in the last block and one in the options.
        var content = OldContent.Replace("\"partialCut\":true", "\"partialCut\":true,\"noSuchField\":1", StringComparison.Ordinal);
        var options = OldOptions.Replace("\"autoCut\":false", "\"autoCut\":false,\"noSuchSetting\":[1]", StringComparison.Ordinal);
        var arguments = $$"""{"content":{{content}},"options":{{options}}}""";
        Assert.Contains("noSuchField", arguments);
        Assert.Contains("noSuchSetting", arguments);

        var (isError, text) = await _client.CallToolAsync("print", arguments);

        Assert.False(isError, text);
        Assert.Equal("Printed.", text);
        Assert.True(app.Printer.Jobs.TryDequeue(out var job));
        Assert.Equal([PrintStyle.Italic, PrintStyle.Bold], job.Content[0].Style);
        Assert.False(job.Content[0].ImageOptions!.HighDensity);
        Assert.Equal(BarcodeType.GS1_DATABAR_OMNIDIRECTIONAL, job.Content[1].BarcodeOptions!.Type);
        Assert.True(job.Content[2].PartialCut);
        Assert.Equal("WPC1250", job.Options!.CodePage);
        Assert.Equal(5, job.Options.FeedLinesAfterPrint);
    }

    // No tool text names a field that does nothing. A model that reads the schema does not learn it.
    [Fact]
    public async Task ToolsList_FieldsWithNoEffect_AreInNoSchemaAndNoText()
    {
        var tools = (await _client.McpAsync("tools/list")).GetRawText();

        Assert.All(
            [HiddenPrintFields.PartialCut, "partial", "WPC1250", "ISO8859_2", "1250", "8859"],
            name => Assert.DoesNotContain(name, tools, StringComparison.OrdinalIgnoreCase));
        Assert.All(
            [HiddenPrintFields.PartialCut, "highDensity", "WPC1250", "ISO8859_2"],
            name => Assert.DoesNotContain(name, PrinterTools.ServerInstructions, StringComparison.OrdinalIgnoreCase));
        // The pruned schema is still the schema of the print tool.
        Assert.Contains("\"useLegacyMode\"", tools);
        Assert.Contains("\"separatorLength\"", tools);
    }

    // highDensity stays in the schema: it is read outside legacy mode. The text says that it does nothing in the default mode.
    [Fact]
    public async Task ToolsList_HighDensity_SaysWhenItIsRead()
    {
        var tools = (await _client.McpAsync("tools/list")).GetProperty("tools");
        var print = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == PrinterTools.PrintName);
        var density = print.GetProperty("inputSchema").GetProperty("properties").GetProperty("content").GetProperty("items").GetProperty("properties")
            .GetProperty("imageOptions").GetProperty("properties").GetProperty("highDensity").GetProperty("description").GetString();

        Assert.StartsWith("Only read when useLegacyMode is false", density);
        Assert.Contains("No effect in legacy mode, the default.", density);
    }

    // feedLinesAfterPrint stays: it feeds, but not lines. The text says so.
    [Fact]
    public async Task ToolsList_FeedLinesAfterPrint_SaysThatItIsNotLines()
    {
        var tools = (await _client.McpAsync("tools/list")).GetProperty("tools");
        var print = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == PrinterTools.PrintName);
        var feed = print.GetProperty("inputSchema").GetProperty("properties").GetProperty("options").GetProperty("properties")
            .GetProperty("feedLinesAfterPrint").GetProperty("description").GetString();

        Assert.Contains("Not lines", feed);
        Assert.Contains("add a LineFeed block", feed);
    }

    // The API names and the numbers of the enums are in stored jobs and in callers: a member with no effect stays.
    [Fact]
    public void Enums_WithMembersOfNoEffect_KeepEveryNameAndNumber()
    {
        Assert.Equal(
            ["Normal", "Bold", "Italic", "Underline", "DoubleHeight", "DoubleWidth", "FontB", "ReverseMode", "UpsideDownMode"],
            Enum.GetValues<PrintStyle>().Select(style => $"{style}"));
        Assert.Equal(Enumerable.Range(0, 9), Enum.GetValues<PrintStyle>().Select(style => (int)style));
        Assert.Equal(
            ["UPC_A", "UPC_E", "EAN13", "EAN8", "CODE39", "CODE128", "ITF", "CODABAR", "GS1_128", "GS1_DATABAR_OMNIDIRECTIONAL"],
            Enum.GetValues<BarcodeType>().Select(type => $"{type}"));
        Assert.Equal(Enumerable.Range(0, 10), Enum.GetValues<BarcodeType>().Select(type => (int)type));
    }
}
