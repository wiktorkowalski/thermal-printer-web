using System.Net;
using System.Text.Json;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Journal;
using ThermalPrinterWeb.Services.Printing;
using static ThermalPrinterWeb.Tests.Support.TestBlocks;

namespace ThermalPrinterWeb.Tests;

// options.feedLinesAfterPrint feeds lines before a cut (issue #44, owner decision of 2026-10-06).
// A job that does not send the field has the bytes from before.
public sealed class FeedLinesTests
{
    private const string PrintUrl = TestHttp.PrintUrl;

    // ESC a 1, ESC ! 0, "ok", LF, ESC ! 0: a Text block with no style.
    private static readonly byte[] OkBlock = [0x1B, 0x61, 1, 0x1B, 0x21, 0, (byte)'o', (byte)'k', 0x0A, 0x1B, 0x21, 0];

    // GS V 65 3 and GS V 66 3: the cut command of every job, from before the field fed lines.
    private static readonly byte[] FullCut = [0x1D, 0x56, 0x41, 3];
    private static readonly byte[] PartialCut = [0x1D, 0x56, 0x42, 3];

    // The whole job from before the change, as the printer got it with no feed field.
    private static readonly byte[] JobFromBefore = [.. Prelude, .. OkBlock, .. FullCut];

    private static byte[] Feed(int lines) => [.. Enumerable.Repeat((byte)0x0A, lines)];

    private static string Job(string? options)
        => $$"""{"content":[{"type":"Text","content":"ok"}]{{(options is null ? "" : $",\"options\":{options}")}}}""";

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"feedLinesAfterPrint":null}""")]
    [InlineData("""{"autoCut":true,"codePage":"PC852"}""")]
    public async Task PostPrinter_NoFeedField_SendsTheBytesFromBefore(string? options)
    {
        await using var printer = new WirePrinter();
        await using var app = new LoopbackPrinterApp(printer.Port);

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, Job(options));

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(JobFromBefore, await printer.NextJobAsync());
    }

    [Fact]
    public async Task BuildDocumentAsync_NoFeedField_SendsTheBytesFromBefore()
    {
        Assert.Equal(JobFromBefore, await JobBytesAsync([Text()]));
        Assert.Equal(JobFromBefore, await JobBytesAsync([Text()], new PrintOptions()));
        // A Cut block: its ESC a 1, then the same cut command.
        Assert.Equal([.. Prelude, .. OkBlock, 0x1B, 0x61, 1, .. FullCut], await JobBytesAsync([Text(), new() { Type = ContentType.Cut }]));
    }

    // The value is a number of lines: that many LF, the bytes of a LineFeed block, then the cut command of before.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(255)]
    public async Task BuildDocumentAsync_FeedField_FeedsThatManyLinesBeforeTheAutoCut(int lines)
    {
        var job = await JobBytesAsync([Text()], new PrintOptions { FeedLinesAfterPrint = lines });

        Assert.Equal([.. Prelude, .. OkBlock, .. Feed(lines), .. FullCut], job);
    }

    // Three lines give the end of the house style, which is read from paper: LF LF LF, GS V 65 3.
    [Fact]
    public async Task BuildDocumentAsync_FeedOfThree_EndsLikeTheHouseStyle()
    {
        var note = await JobBytesAsync(SimpleNote.Build("Title", "Message", new DateOnly(2026, 10, 6)));

        var job = await JobBytesAsync([Text()], new PrintOptions { FeedLinesAfterPrint = SimpleNote.FeedLines });

        Assert.Equal([0x0A, 0x0A, 0x0A, .. FullCut], job[^7..]);
        // The note has the ESC a 1 of its Cut block between the lines and the cut.
        Assert.Equal([0x0A, 0x0A, 0x0A, 0x1B, 0x61, 1, .. FullCut], note[^10..]);
    }

    // Each Cut block feeds the lines; a document with a Cut block gets no auto-cut and no second feed.
    [Fact]
    public async Task BuildDocumentAsync_FeedFieldAndCutBlocks_FeedsBeforeEachCut()
    {
        List<PrintContent> content = [Text(), new() { Type = ContentType.Cut }, Text(), new() { Type = ContentType.Cut, PartialCut = true }];

        var job = await JobBytesAsync(content, new PrintOptions { FeedLinesAfterPrint = 2 });

        byte[] center = [0x1B, 0x61, 1];
        Assert.Equal([.. Prelude, .. OkBlock, .. center, .. Feed(2), .. FullCut, .. OkBlock, .. center, .. Feed(2), .. PartialCut], job);
    }

    // No cut, no feed: the field is the feed before a cut.
    [Fact]
    public async Task BuildDocumentAsync_FeedFieldWithoutAnyCut_FeedsNothing()
    {
        var job = await JobBytesAsync([Text()], new PrintOptions { AutoCut = false, FeedLinesAfterPrint = 3 });

        Assert.Equal([.. Prelude, .. OkBlock], job);
    }

    // A block sets the size back to 1 x 1 after its text, so the feed lines are plain lines after a block of any size.
    [Fact]
    public async Task BuildDocumentAsync_FeedAfterALargeText_ComesAfterTheSizeReset()
    {
        var big = new PrintContent { Type = ContentType.Text, Content = "BIG", Size = new TextSize { Width = 8, Height = 8 } };
        var tall = Text("tall", PrintStyle.DoubleHeight);

        var afterBig = await JobBytesAsync([big], new PrintOptions { FeedLinesAfterPrint = 3 });
        var afterTall = await JobBytesAsync([tall], new PrintOptions { FeedLinesAfterPrint = 3 });

        // GS ! 0, ESC ! 0, then the feed and the cut.
        Assert.Equal([0x1D, 0x21, 0, 0x1B, 0x21, 0, .. Feed(3), .. FullCut], afterBig[^13..]);
        Assert.Equal([0x1B, 0x21, 0, .. Feed(3), .. FullCut], afterTall[^10..]);
    }

    // The paper estimate counts the feed in lines of the line spacing of the job.
    [Theory]
    [InlineData(null, null, false, 29)]
    [InlineData(3, null, false, 29 + 3 * 29)]
    [InlineData(3, 60, false, 60 + 3 * 60)]
    [InlineData(255, null, false, 29 + 255 * 29)]
    // A Cut block also counts its cut command and the way to the cutter: 3 + 124 dots.
    [InlineData(null, null, true, 29 + 127)]
    [InlineData(0, null, true, 29 + 127)]
    [InlineData(3, null, true, 29 + 3 * 29 + 127)]
    public async Task PostPrinter_FeedField_CountsAsPaper(int? feed, int? lineSpacing, bool cutBlock, int expectedDots)
    {
        await using var app = new NoPrinterApp();
        var json = JsonSerializer.Serialize(new
        {
            content = cutBlock ? new object[] { new { type = "Text", content = "ok" }, new { type = "Cut" } } : [new { type = "Text", content = "ok" }],
            options = new { feedLinesAfterPrint = feed, defaultLineSpacing = lineSpacing }
        });

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, json);

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(expectedDots, Assert.Single(await app.JournalRowsAsync()).PaperDots);
    }

    // Over HTTP: the largest feed passes at the default line spacing; with a larger spacing the paper limit answers 400.
    [Theory]
    [InlineData("""{"feedLinesAfterPrint":255}""", null)]
    [InlineData("""{"feedLinesAfterPrint":125,"defaultLineSpacing":255}""", "options.feedLinesAfterPrint: the document is over the limit of 32000 dots of paper (4 m)")]
    [InlineData("""{"feedLinesAfterPrint":256}""", "options.feedLinesAfterPrint 256 is outside the range 0 to 255")]
    public async Task PostPrinter_LargeFeed_IsLimitedByThePaper(string options, string? expectedError)
    {
        await using var app = new NoPrinterApp();

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, Job(options));

        if (expectedError is null)
        {
            Assert.True(status == HttpStatusCode.OK, body);
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var answer = JsonSerializer.Deserialize<PrintResponse>(body, PrintJobEntry.ApiJson)!;
        Assert.Equal(expectedError, answer.Error);
        Assert.Equal("validation", answer.Type);
    }

    // The journal stores the field as sent, and a reprint feeds the same lines: the same bytes.
    [Fact]
    public async Task PrintThenReprint_FeedField_IsStoredAndFedAgain()
    {
        await using var printer = new WirePrinter();
        await using var app = new LoopbackPrinterApp(printer.Port);
        var client = app.CreateClient();

        await client.SendJsonAsync(HttpMethod.Post, PrintUrl, Job("""{"feedLinesAfterPrint":3}"""));
        var first = await printer.NextJobAsync();
        await client.SendJsonAsync(HttpMethod.Post, PrintUrl, Job("{}"));
        var plain = await printer.NextJobAsync();
        var rows = await app.JournalRowsAsync();
        var withFeed = rows.Single(row => row.Payload.Options!.Contains("\"feedLinesAfterPrint\":3"));
        var withoutFeed = rows.Single(row => row.Payload.Options!.Contains("\"feedLinesAfterPrint\":null"));
        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{withFeed.Id}/reprint");
        var second = await printer.NextJobAsync();
        await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{withoutFeed.Id}/reprint");
        var plainAgain = await printer.NextJobAsync();

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal([.. Prelude, .. OkBlock, .. Feed(3), .. FullCut], first);
        Assert.Equal(first, second);
        Assert.Equal(JobFromBefore, plain);
        Assert.Equal(JobFromBefore, plainAgain);
    }

    // A row from before the change holds the number that the model had then: 3 when the caller sent an options object
    // without the field. A reprint reads it as 3 lines (accepted by the owner). A row with no options keeps its bytes.
    [Theory]
    [InlineData("""{"codePage":null,"defaultLineSpacing":null,"autoCut":true,"feedLinesAfterPrint":3}""", 3)]
    [InlineData(null, 0)]
    public async Task Reprint_RowStoredBeforeTheChange_FeedsTheStoredNumberAsLines(string? storedOptions, int lines)
    {
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
                CreatedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                Transport = "http",
                Result = JobResult.Printed,
                HttpStatus = 200,
                AppVersion = "before-44",
                BlockCount = 1,
                Payload = new PrintJobPayload
                {
                    JobId = id,
                    Headers = "{}",
                    Blocks = """[{"type":"Text","content":"ok","alignment":"Center","style":null,"size":null,"lines":1,"partialCut":false,"separatorChar":"=","separatorLength":32}]""",
                    Options = storedOptions
                }
            });
            await db.SaveChangesAsync();
        }

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{id}/reprint");
        var reprinted = await printer.NextJobAsync();

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal([.. Prelude, .. OkBlock, .. Feed(lines), .. FullCut], reprinted);
    }

    [Fact]
    public async Task McpPrint_FeedField_FeedsLinesLikeHttp()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();

        var (isError, text) = await client.CallToolAsync("print", """{"content":[{"type":"Text","content":"ok"}],"options":{"feedLinesAfterPrint":3}}""");
        var (_, plainText) = await client.CallToolAsync("print", """{"content":[{"type":"Text","content":"ok"}]}""");
        var (_, nullText) = await client.CallToolAsync("print", """{"content":[{"type":"Text","content":"ok"}],"options":{"feedLinesAfterPrint":null}}""");
        var rows = await app.JournalRowsAsync();

        Assert.False(isError, text);
        Assert.Equal("Printed.", text);
        Assert.Equal("Printed.", plainText);
        Assert.Equal("Printed.", nullText);
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, row => row.Payload.Bytes!.AsSpan().SequenceEqual([.. Prelude, .. OkBlock, .. Feed(3), .. FullCut]));
        Assert.Equal(2, rows.Count(row => row.Payload.Bytes!.AsSpan().SequenceEqual(JobFromBefore)));
    }

    // The number in the caller texts is the feed of the house style.
    [Fact]
    public void CallerTexts_FeedBeforeACut_NameTheFeedOfTheHouseStyle()
    {
        Assert.Contains($"set options.feedLinesAfterPrint to {SimpleNote.FeedLines} ", PrinterTools.CutFeedRule);
        Assert.Contains($"a LineFeed block of {SimpleNote.FeedLines} lines, not both", PrinterTools.CutFeedRule);
        Assert.Contains(PrinterTools.CutFeedRule, PrinterTools.ServerInstructions);
        var description = typeof(PrintOptions).GetProperty(nameof(PrintOptions.FeedLinesAfterPrint))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        Assert.Contains($"send {SimpleNote.FeedLines} to keep that line whole", description);
    }
}
