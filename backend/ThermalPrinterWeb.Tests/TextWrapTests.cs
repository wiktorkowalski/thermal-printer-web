using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;
using static ThermalPrinterWeb.Tests.Support.TestBlocks;

namespace ThermalPrinterWeb.Tests;

// "wrap": true on a Text block (#55): the server breaks the lines at spaces, with the wrap of simple mode.
// A block without the flag prints as before.
public sealed class TextWrapTests
{
    private const string PrintUrl = TestHttp.PrintUrl;

    private const string Prose = "Zażółć gęślą jaźń, a potem sprawdź, czy drukarka łamie wiersze na spacjach. "
        + "Bardzo długie słowo Konstantynopolitańczykowianeczkówna też musi się zmieścić.";

    private static readonly PrintOptions NoCut = new() { AutoCut = false };

    private static PrintContent Block(string content, bool wrap, TextSize? size, PrintStyle[] style)
    {
        var block = Text(content, style);
        block.Size = size;
        block.Wrap = wrap ? true : null;
        return block;
    }

    private static PrintContent Wrapped(string content, TextSize? size = null, params PrintStyle[] style) => Block(content, wrap: true, size, style);

    private static PrintContent Unwrapped(string content, TextSize? size = null, params PrintStyle[] style) => Block(content, wrap: false, size, style);

    // One centered plain block with no cut: ESC a 1, ESC ! 0, the text, LF, ESC ! 0.
    private static byte[] PlainJob(string printed) => [.. Prelude, 0x1B, 0x61, 1, 0x1B, 0x21, 0x00, .. Pc852Bytes(printed), 0x0A, 0x1B, 0x21, 0x00];

    private static string JobJson(string wrapField)
        => """{"content":[{"type":"Text","content":"one two three four five six seven eight nine ten eleven twelve"WRAP}],"options":{"autoCut":false}}"""
            .Replace("WRAP", wrapField, StringComparison.Ordinal);

    // No flag, false and null: the bytes of the same block from before the field.
    [Fact]
    public async Task JobBytes_NoWrapFlag_AreTheBytesFromBeforeTheField()
    {
        var longLine = new string('x', 47) + " yy zz";
        var noFlag = new PrintContent { Type = ContentType.Text, Content = longLine };
        var isFalse = new PrintContent { Type = ContentType.Text, Content = longLine, Wrap = false };
        var isNull = new PrintContent { Type = ContentType.Text, Content = longLine, Wrap = null };

        var expected = PlainJob(longLine);
        Assert.Equal(expected, await JobBytesAsync([noFlag], NoCut));
        Assert.Equal(expected, await JobBytesAsync([isFalse], NoCut));
        Assert.Equal(expected, await JobBytesAsync([isNull], NoCut));
    }

    // The JSON forms of the field, through the real pipeline.
    [Fact]
    public async Task PostPrinter_WrapFormsOfTheJson_WrapOnlyForTrue()
    {
        const string AsSent = "one two three four five six seven eight nine ten eleven twelve";
        const string Broken = "one two three four five six seven eight nine ten\neleven twelve";
        (string Json, string Printed)[] cases =
        [
            (JobJson(""), AsSent),
            (JobJson(""","wrap":false"""), AsSent),
            (JobJson(""","wrap":null"""), AsSent),
            (JobJson(""","wrap":true"""), Broken)
        ];
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();

        foreach (var (json, printed) in cases)
        {
            var (status, body) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, json);

            Assert.True(status == HttpStatusCode.OK, body);
            Assert.Equal(PlainJob(printed), (await app.JournalRowsAsync())[^1].Payload.Bytes);
        }
    }

    public static TheoryData<int, int?, int?, PrintStyle[]> Styles() => new()
    {
        { 48, null, null, [] },
        { 24, null, null, [PrintStyle.DoubleWidth] },
        { 48, null, null, [PrintStyle.DoubleHeight, PrintStyle.Bold] },
        { 64, null, null, [PrintStyle.FontB] },
        { 32, null, null, [PrintStyle.FontB, PrintStyle.DoubleWidth] },
        { 16, 3, 3, [] },
        { 32, 2, 3, [PrintStyle.FontB] },
        { 21, 3, 1, [PrintStyle.FontB] },
        { 6, 8, 8, [PrintStyle.Bold] },
        // A block with a size ignores DoubleWidth: the size wins.
        { 48, 1, 1, [PrintStyle.DoubleWidth] },
        { 16, 3, 2, [PrintStyle.DoubleWidth] }
    };

    // The column limit comes from the font and the width of the block. The flagged block prints the bytes
    // of the same block with its lines broken by hand at that limit.
    [Theory]
    [MemberData(nameof(Styles))]
    public async Task JobBytes_WrapFlag_BreaksAtTheColumnsOfTheFontAndTheWidth(int columns, int? width, int? height, PrintStyle[] style)
    {
        TextSize? Size() => width is null ? null : new TextSize { Width = width.Value, Height = height!.Value };
        var byHand = WordWrap.Wrap(Prose, columns);

        var flagged = await JobBytesAsync([Wrapped(Prose, Size(), style)], NoCut);

        Assert.Equal(await JobBytesAsync([Unwrapped(byHand, Size(), style)], NoCut), flagged);
        Assert.Contains('\n', byHand);
        Assert.All(byHand.Split('\n'), line => Assert.InRange(line.Length, 1, columns));
        Assert.Equal(columns, PaperLength.Columns([.. style], Size()));
    }

    // The block does not change: the journal stores it as the caller sent it.
    [Fact]
    public async Task HandleAsync_WrapFlag_LeavesTheBlockAsSent()
    {
        var block = Wrapped(Prose);

        await new TextBlockHandler().HandleAsync(block, NewContext(Pc852));

        Assert.Equal(Prose, block.Content);
        Assert.True(block.Wrap);
    }

    // A Separator does not wrap, and no other block type reads the flag.
    [Fact]
    public async Task JobBytes_WrapFlagOnOtherBlockTypes_IsNotRead()
    {
        List<PrintContent> Blocks(bool? wrap) =>
        [
            new() { Type = ContentType.Separator, SeparatorLength = 60, Wrap = wrap },
            new() { Type = ContentType.LineFeed, Lines = 2, Wrap = wrap },
            new() { Type = ContentType.QRCode, Content = "one two three four five six seven eight nine ten eleven twelve", Wrap = wrap },
            new() { Type = ContentType.Barcode, Content = "BOX 0007", Wrap = wrap }
        ];

        Assert.Equal(await JobBytesAsync(Blocks(null), NoCut), await JobBytesAsync(Blocks(true), NoCut));
    }

    // The limits of a Text block count the text after the wrap, as in simple mode.
    [Fact]
    public async Task PrintAsync_WrappedTextOverTheLimits_IsAValidationFailure()
    {
        // 10,000 characters in one word: 208 line breaks are added at 48 columns.
        var oneWord = new string('x', TextBlockHandler.MaxLength);
        // 501 words that are one line each at 6 columns.
        var manyLines = string.Join(' ', Enumerable.Repeat("xxxxxx", TextBlockHandler.MaxLines + 1));
        var eightByOne = new TextSize { Width = 8, Height = 1 };

        var fits = await NewService().PrintAsync([Text("ok"), Unwrapped(oneWord)]);
        var tooLong = await NewService().PrintAsync([Text("ok"), Wrapped(oneWord)]);
        var fewLines = await NewService().PrintAsync([Unwrapped(manyLines)]);
        var tooManyLines = await NewService().PrintAsync([Wrapped(manyLines, eightByOne)]);

        Assert.Equal(PrintResult.Ok, fits);
        Assert.Equal(PrintResult.Invalid($"Block 1 (Text): text length {TextBlockHandler.MaxLength + 208} is over the limit of {TextBlockHandler.MaxLength}"), tooLong);
        Assert.Equal(PrintResult.Ok, fewLines);
        Assert.Equal(
            PrintResult.Invalid($"Block 0 (Text): text line count {TextBlockHandler.MaxLines + 1} is over the limit of {TextBlockHandler.MaxLines}"),
            tooManyLines);
    }

    // The paper limit counts the lines after the wrap: 410 words of 32 columns are 410 tall lines.
    [Fact]
    public async Task PrintAsync_WrappedLines_AreLimitedByThePaper()
    {
        var tall = new TextSize { Width = 2, Height = 3 };
        var most = PaperLength.MaxDots / PaperLength.LineDots(null, tall.Height);
        string Words(int count) => string.Join(' ', Enumerable.Repeat("xxxxxxxxxxxxxxxxx", count));

        var fits = await NewService().PrintAsync([Wrapped(Words(most), tall, PrintStyle.FontB)], NoCut);
        var over = await NewService().PrintAsync([Wrapped(Words(most + 1), tall, PrintStyle.FontB)], NoCut);

        Assert.Equal(PrintResult.Ok, fits);
        Assert.Equal(PrintResult.Invalid(PayloadErrorTests.OverPaper(0, ContentType.Text)), over);
    }

    // A size outside the range is named, with or without the flag.
    [Fact]
    public async Task PrintAsync_WrapFlagWithASizeOutsideTheRange_NamesTheSize()
    {
        var result = await NewService().PrintAsync([Wrapped(Secret, new TextSize { Width = 9, Height = 1 })]);

        Assert.Equal(PrintResult.Invalid("Block 0 (Text): size.width 9 is outside the range 1 to 8"), result);
    }

    // The request body limit lets 30 MB of text in. A text over the limit of a Text block is rejected before the wrap reads it.
    [Fact]
    public async Task PrintAsync_ThirtyMegabytesOfTextWithTheFlag_IsRejectedWithNoWrap()
    {
        // The slowest character to measure.
        var huge = new string('ż', 15_000_000);
        var clock = Stopwatch.StartNew();

        var result = await NewService().PrintAsync([Wrapped(huge)]);

        Assert.Equal(PrintResult.Invalid($"Block 0 (Text): text length {huge.Length} is over the limit of {TextBlockHandler.MaxLength}"), result);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Took {clock.Elapsed}.");
    }

    public static TheoryData<string, bool> LargestJobs() => new()
    {
        // One character and spaces that are not printed: each block is one line, so every block is wrapped and the job prints.
        { "x" + new string(' ', TextBlockHandler.MaxLength - 1), true },
        // Emoji with no space, each one measured: the paper limit stops the job after a few blocks.
        { string.Concat(Enumerable.Repeat("😀", TextBlockHandler.MaxLength / 4)), false }
    };

    // The most work one job can ask for: 500 Text blocks of 10,000 characters, each with the flag.
    [Theory]
    [MemberData(nameof(LargestJobs))]
    public async Task PrintAsync_FiveHundredWrappedBlocksAtTheTextLimit_IsBounded(string text, bool prints)
    {
        var blocks = Enumerable.Range(0, PrinterService.MaxBlocks).Select(_ => Wrapped(text, null, PrintStyle.FontB)).ToList();
        var clock = Stopwatch.StartNew();

        var result = await NewService().PrintAsync(blocks);

        Assert.Equal(prints, result.Success);
        if (!prints)
            Assert.EndsWith($"the document is over the limit of {PaperLength.MaxDots} dots of paper ({PaperLength.MaxDots / PaperLength.DotsPerMetre} m)", result.Error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Took {clock.Elapsed}.");
    }

    // Through the real pipeline: the printer gets the broken lines, the journal keeps the block as sent,
    // and a reprint wraps it again.
    [Fact]
    public async Task PostPrinter_WrapFlag_ReachesTheBytesTheJournalAndAReprint()
    {
        await using var printer = new WirePrinter();
        await using var app = new LoopbackPrinterApp(printer.Port);
        var client = app.CreateClient();
        var json = JsonSerializer.Serialize(new
        {
            content = new object[] { new { type = "Text", content = Prose, style = new[] { "FontB" }, size = new { width = 2, height = 3 }, wrap = true } },
            options = new { autoCut = false }
        });

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        var first = await printer.NextJobAsync();
        var job = Assert.Single(await app.JournalRowsAsync());
        var (reprintStatus, reprintBody) = await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{job.Id}/reprint");
        var second = await printer.NextJobAsync();

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.True(reprintStatus == HttpStatusCode.OK, reprintBody);
        Assert.Equal(
            [.. Prelude, 0x1B, 0x61, 1, 0x1B, 0x21, 0x01, 0x1D, 0x21, 0x12, .. Pc852Bytes(WordWrap.Wrap(Prose, SimpleNote.BodyColumns)), 0x0A, 0x1D, 0x21, 0x00, 0x1B, 0x21, 0x00],
            first);
        Assert.Equal(first, second);
        Assert.Equal(6 * PaperLength.LineDots(null, 3), job.PaperDots);

        // The stored block is the block of the caller: the flag, and the text with no line break of the server.
        var stored = Assert.Single(JsonSerializer.Deserialize<List<PrintContent>>(job.Payload.Blocks!, PrintJobEntry.ApiJson)!);
        Assert.True(stored.Wrap);
        Assert.Equal(Prose, stored.Content);
        // The title and the search text are the text as sent too.
        Assert.Equal(Prose, job.Payload.PlainText);
        Assert.Equal(Prose[..PrintJobEntry.MaxTitleLength], job.Title);
    }

    // A row from before the field: its stored block has no "wrap". It reads and reprints as before, with no line break added.
    [Fact]
    public async Task Reprint_StoredJobFromBeforeTheField_PrintsTheTextAsStored()
    {
        const string LongLine = "one two three four five six seven eight nine ten eleven twelve";
        var id = Guid.CreateVersion7();
        await using var printer = new WirePrinter();
        await using var app = new LoopbackPrinterApp(printer.Port);
        var client = app.CreateClient();
        // The first request opens the journal.
        await client.GetAsync($"{PrintUrl}/jobs");
        await app.JournalIdleAsync();
        await using (var db = app.JournalDb())
        {
            db.PrintJobs.Add(new PrintJob
            {
                Id = id,
                CreatedAt = DateTime.UtcNow,
                Transport = "http",
                Result = JobResult.Printed,
                HttpStatus = 200,
                Title = LongLine,
                BlockCount = 1,
                AppVersion = "test",
                Payload = new PrintJobPayload
                {
                    JobId = id,
                    Headers = "{}",
                    Blocks = $$"""[{"type":"Text","content":"{{LongLine}}","alignment":"Center","style":null,"size":null,"lines":1,"partialCut":false,"separatorChar":"=","separatorLength":32}]""",
                    Options = """{"autoCut":false}""",
                    PlainText = LongLine
                }
            });
            await db.SaveChangesAsync();
        }

        var (jobStatus, jobBody) = await client.SendJsonAsync(HttpMethod.Get, $"{PrintUrl}/jobs/{id}");
        var (reprintStatus, reprintBody) = await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{id}/reprint");
        var bytes = await printer.NextJobAsync();

        Assert.True(jobStatus == HttpStatusCode.OK, jobBody);
        Assert.False(JsonDocument.Parse(jobBody).RootElement.GetProperty("blocks")[0].TryGetProperty("wrap", out _));
        Assert.True(reprintStatus == HttpStatusCode.OK, reprintBody);
        Assert.Equal(PlainJob(LongLine), bytes);
    }

    [Fact]
    public async Task McpPrint_WrapFlag_FollowsTheSameRules()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();

        var (_, printed) = await client.CallToolAsync("print", JobJson(""","wrap":true"""));
        var (isError, wrongType) = await client.CallToolAsync("print", JobJson(""","wrap":"yes" """));
        var job = (await app.JournalRowsAsync())[0];

        Assert.Equal("Printed.", printed);
        Assert.Contains("\"wrap\":true", job.Payload.Blocks);
        byte[] lastLines = [.. Pc852Bytes("nine ten\neleven twelve\n"), 0x1B, 0x21, 0x00];
        Assert.Equal(lastLines, job.Payload.Bytes![^lastLines.Length..]);
        Assert.True(isError);
        Assert.StartsWith("Wrong arguments for 'print': the value at content[0].wrap has the wrong JSON type or is not a known name.", wrongType);
    }

    // The texts a model reads say what the flag does, and that the default did not change.
    [Fact]
    public async Task ToolsList_Print_SaysThatTheFlagMakesTheServerBreakTheLines()
    {
        await using var app = new NoPrinterApp();
        var tools = (await app.CreateClient().McpAsync("tools/list")).GetProperty("tools");
        var tool = tools.EnumerateArray().Single(candidate => candidate.GetProperty("name").GetString() == "print");
        var block = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("content").GetProperty("items").GetProperty("properties");
        var wrap = block.GetProperty("wrap").GetProperty("description").GetString();
        var description = tool.GetProperty("description").GetString();

        Assert.StartsWith("Text blocks only: true makes the server break each line", wrap);
        Assert.Contains($"The limits of {TextBlockHandler.MaxLength} characters and {TextBlockHandler.MaxLines} lines count the text with the line breaks that the server adds.", wrap);
        Assert.Contains("Default false: the text prints as sent and the printer wraps in the middle of a word.", wrap);
        Assert.Contains("a longer line wraps in the middle of a word unless 'wrap' is true", block.GetProperty("content").GetProperty("description").GetString());
        Assert.Contains("longer lines wrap in the middle of a word, so keep each line within the limit", description);
        Assert.Contains("or set \"wrap\":true on the Text block: then the server breaks each longer line at a space", description);
        Assert.Contains("a longer line wraps in the middle of a word, so break lines yourself or set \"wrap\":true on a Text block of print", Mcp.PrinterTools.ServerInstructions);
    }
}
