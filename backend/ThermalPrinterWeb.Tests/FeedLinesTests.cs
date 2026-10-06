using System.Net;
using System.Text.Json;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Journal;
using ThermalPrinterWeb.Services.Printing;
using static ThermalPrinterWeb.Tests.Support.TestBlocks;

namespace ThermalPrinterWeb.Tests;

// options.feedLinesAfterPrint feeds lines before a cut (issue #44, owner decision of 2026-10-06).
// A job that does not send the field gets 3 empty lines before each cut, the lines of the LineFeed blocks right before the cut included
// (owner decision of 2026-10-06, the second one).
public sealed class FeedLinesTests
{
    private const string PrintUrl = TestHttp.PrintUrl;

    // ESC a 1, ESC ! 0, "ok", LF, ESC ! 0: a Text block with no style.
    private static readonly byte[] OkBlock = [0x1B, 0x61, 1, 0x1B, 0x21, 0, (byte)'o', (byte)'k', 0x0A, 0x1B, 0x21, 0];

    // GS V 65 3 and GS V 66 3: the cut command of every job, from before the field fed lines.
    private static readonly byte[] FullCut = [0x1D, 0x56, 0x41, 3];
    private static readonly byte[] PartialCut = [0x1D, 0x56, 0x42, 3];

    // ESC a 1: every block starts with its alignment.
    private static readonly byte[] Center = [0x1B, 0x61, 1];

    // A job that ends with text and sends no feed field: the default lines, then the cut command.
    private static readonly byte[] JobWithDefaultFeed = [.. Prelude, .. OkBlock, .. Feed(CutFeed.DefaultLines), .. FullCut];

    private static byte[] Feed(int lines) => [.. Enumerable.Repeat((byte)0x0A, lines)];

    private static PrintContent LineFeed(int? lines) => new() { Type = ContentType.LineFeed, Lines = lines };

    private static PrintContent Cut() => new() { Type = ContentType.Cut };

    private static string Job(string? options)
        => $$"""{"content":[{"type":"Text","content":"ok"}]{{(options is null ? "" : $",\"options\":{options}")}}}""";

    [Fact]
    public void DefaultLines_IsThree_AndTheHouseStyleUsesIt()
    {
        Assert.Equal(3, CutFeed.DefaultLines);
        Assert.Equal(CutFeed.DefaultLines, SimpleNote.FeedLines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"feedLinesAfterPrint":null}""")]
    [InlineData("""{"autoCut":true,"codePage":"PC852"}""")]
    public async Task PostPrinter_NoFeedField_FeedsTheDefaultLines(string? options)
    {
        await using var printer = new WirePrinter();
        await using var app = new LoopbackPrinterApp(printer.Port);

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, Job(options));

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(JobWithDefaultFeed, await printer.NextJobAsync());
    }

    [Fact]
    public async Task BuildDocumentAsync_NoFeedFieldAndTextAtTheEnd_FeedsThreeLines()
    {
        var withCutBlock = await JobBytesAsync([Text(), Cut()]);
        var empty = await JobBytesAsync([]);

        Assert.Equal(JobWithDefaultFeed, await JobBytesAsync([Text()]));
        Assert.Equal(JobWithDefaultFeed, await JobBytesAsync([Text()], new PrintOptions()));
        // A Cut block: its ESC a 1, the lines, then the same cut command.
        Assert.Equal([.. Prelude, .. OkBlock, .. Center, .. Feed(3), .. FullCut], withCutBlock);
        // An empty document has no empty line either.
        Assert.Equal([.. Prelude, .. Feed(3), .. FullCut], empty);
    }

    // The LineFeed blocks at the end count toward the 3 lines: the cut adds what is missing, and nothing when 3 or more are there.
    // A LineFeed block with no lines field is one line; a negative number is none.
    [Theory]
    [InlineData(new[] { 1 }, 2)]
    [InlineData(new[] { 2 }, 1)]
    [InlineData(new[] { 3 }, 0)]
    [InlineData(new[] { 5 }, 0)]
    [InlineData(new[] { 0 }, 3)]
    [InlineData(new[] { -4 }, 3)]
    [InlineData(new[] { 1, 1 }, 1)]
    [InlineData(new[] { 2, 1 }, 0)]
    public async Task BuildDocumentAsync_NoFeedFieldAndLineFeedAtTheEnd_AddsTheMissingLines(int[] trailing, int added)
    {
        List<PrintContent> content = [Text(), .. trailing.Select(lines => LineFeed(lines))];
        byte[] blocks = [.. OkBlock, .. trailing.SelectMany(lines => (byte[])[.. Center, .. Feed(Math.Max(lines, 0))])];

        var autoCut = await JobBytesAsync(content);
        var cutBlock = await JobBytesAsync([.. content, Cut()]);

        Assert.Equal([.. Prelude, .. blocks, .. Feed(added), .. FullCut], autoCut);
        Assert.Equal([.. Prelude, .. blocks, .. Center, .. Feed(added), .. FullCut], cutBlock);
    }

    [Fact]
    public async Task BuildDocumentAsync_NoFeedFieldAndLineFeedWithNoLinesField_CountsOneLine()
    {
        var job = await JobBytesAsync([Text(), LineFeed(null)]);

        Assert.Equal([.. Prelude, .. OkBlock, .. Center, .. Feed(1), .. Feed(2), .. FullCut], job);
    }

    // The count is of the blocks right before the cut: a LineFeed block before the last printed block does not count,
    // and an empty line inside a Text block does not count.
    [Fact]
    public async Task BuildDocumentAsync_NoFeedField_CountsOnlyTheLineFeedBlocksRightBeforeTheCut()
    {
        var earlier = await JobBytesAsync([LineFeed(3), Text()]);
        var inside = await JobBytesAsync([Text("ok\n\n\n")]);

        Assert.Equal([.. Prelude, .. Center, .. Feed(3), .. OkBlock, .. Feed(3), .. FullCut], earlier);
        Assert.Equal([0x0A, 0x0A, 0x0A, 0x0A, 0x1B, 0x21, 0, .. Feed(3), .. FullCut], inside[^14..]);
    }

    // Each cut has its own count: a Cut block starts a new strip.
    [Fact]
    public async Task BuildDocumentAsync_NoFeedFieldAndCutBlocks_CountsTheLinesBeforeEachCut()
    {
        List<PrintContent> content = [Text(), LineFeed(3), Cut(), Text(), LineFeed(1), Cut(), Text(), Cut(), Cut()];

        var job = await JobBytesAsync(content, new PrintOptions { AutoCut = false });

        Assert.Equal(
            [
                .. Prelude,
                .. OkBlock, .. Center, .. Feed(3), .. Center, .. FullCut,
                .. OkBlock, .. Center, .. Feed(1), .. Center, .. Feed(2), .. FullCut,
                .. OkBlock, .. Center, .. Feed(3), .. FullCut,
                .. Center, .. Feed(3), .. FullCut
            ],
            job);
    }

    // A Signal and a CodePage block move no paper: the empty lines before them still count. After a printed block they add no line.
    [Fact]
    public async Task BuildDocumentAsync_NoFeedField_SignalAndCodePageBlocksKeepTheCount()
    {
        var codePage = new PrintContent { Type = ContentType.CodePage, Content = "PC852" };
        var none = new PrintOptions { FeedLinesAfterPrint = 0 };
        var three = new PrintOptions { FeedLinesAfterPrint = 3 };

        foreach (var block in new[] { Signal(), codePage })
        {
            Assert.Equal(await JobBytesAsync([Text(), LineFeed(3), block], none), await JobBytesAsync([Text(), LineFeed(3), block]));
            Assert.Equal(await JobBytesAsync([Text(), LineFeed(3), block, Cut()], none), await JobBytesAsync([Text(), LineFeed(3), block, Cut()]));
            Assert.Equal(await JobBytesAsync([Text(), LineFeed(2), block, LineFeed(1)], none), await JobBytesAsync([Text(), LineFeed(2), block, LineFeed(1)]));
            Assert.Equal(await JobBytesAsync([Text(), block], three), await JobBytesAsync([Text(), block]));
        }
    }

    // A number that is sent is added as sent: the content is not read. 0 is the way to get the cut command alone.
    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 3)]
    [InlineData(2, 1)]
    [InlineData(0, 0)]
    public async Task BuildDocumentAsync_FeedFieldAndLineFeedAtTheEnd_AddsTheNumberAsSent(int sent, int trailing)
    {
        var job = await JobBytesAsync([Text(), LineFeed(trailing)], new PrintOptions { FeedLinesAfterPrint = sent });

        Assert.Equal([.. Prelude, .. OkBlock, .. Center, .. Feed(trailing), .. Feed(sent), .. FullCut], job);
    }

    // No cut, no feed: also for the default lines.
    [Fact]
    public async Task BuildDocumentAsync_NoFeedFieldWithoutAnyCut_FeedsNothing()
    {
        var job = await JobBytesAsync([Text()], new PrintOptions { AutoCut = false });

        Assert.Equal([.. Prelude, .. OkBlock], job);
    }

    // The paths that must keep their bytes: the house style, a receipt (a feed of 0) and the editor (a feed of 3).
    [Fact]
    public async Task BuildDocumentAsync_HouseStyleReceiptAndEditor_KeepTheirBytes()
    {
        var note = SimpleNote.Build("Title", "Message", new DateOnly(2026, 10, 6));
        List<PrintContent> receipt = [Text(), LineFeed(3), Cut()];
        byte[] receiptBytes = [.. Prelude, .. OkBlock, .. Center, .. Feed(3), .. Center, .. FullCut];

        // The note sends no options: its LineFeed block of 3 lines is the whole feed, with and without the default.
        Assert.Equal(await JobBytesAsync(note, new PrintOptions { FeedLinesAfterPrint = 0 }), await JobBytesAsync(note));
        Assert.Equal(receiptBytes, await JobBytesAsync(receipt, new PrintOptions { CodePage = "PC852", AutoCut = true, FeedLinesAfterPrint = 0 }));
        Assert.Equal(JobWithDefaultFeed, await JobBytesAsync([Text()], new PrintOptions { CodePage = "PC852", AutoCut = true, FeedLinesAfterPrint = 3 }));
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
    [InlineData(0, null, false, 29)]
    [InlineData(3, null, false, 29 + 3 * 29)]
    [InlineData(3, 60, false, 60 + 3 * 60)]
    [InlineData(255, null, false, 29 + 255 * 29)]
    // No feed field: the 3 default lines count the same way.
    [InlineData(null, null, false, 29 + 3 * 29)]
    [InlineData(null, 60, false, 60 + 3 * 60)]
    [InlineData(null, null, true, 29 + 3 * 29 + 127)]
    // A Cut block also counts its cut command and the way to the cutter: 3 + 124 dots.
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

    // No feed field: the lines that the cut adds count as paper, the lines of a LineFeed block count once.
    [Theory]
    [InlineData(1, 29 + 1 * 29 + 2 * 29)]
    [InlineData(3, 29 + 3 * 29)]
    [InlineData(5, 29 + 5 * 29)]
    public async Task PostPrinter_NoFeedFieldAndLineFeedAtTheEnd_CountsThePaperOnce(int trailing, int expectedDots)
    {
        await using var app = new NoPrinterApp();
        var json = JsonSerializer.Serialize(new { content = new object[] { new { type = "Text", content = "ok" }, new { type = "LineFeed", lines = trailing } } });

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, json);

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(expectedDots, Assert.Single(await app.JournalRowsAsync()).PaperDots);
    }

    // Over HTTP: the largest feed passes at the default line spacing; with a larger spacing the paper limit answers 400.
    // PayloadErrorTests.PaperJobs pins the 400 of a job with no feed field that is over the limit only with its default lines.
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
        Assert.Equal(JobWithDefaultFeed, plain);
        Assert.Equal(JobWithDefaultFeed, plainAgain);
    }

    // The same for a feed of 0: the stored 0 is not read as "not sent", so the reprint has the cut command alone.
    [Fact]
    public async Task PrintThenReprint_FeedOfZero_StaysZero()
    {
        await using var printer = new WirePrinter();
        await using var app = new LoopbackPrinterApp(printer.Port);
        var client = app.CreateClient();

        await client.SendJsonAsync(HttpMethod.Post, PrintUrl, Job("""{"feedLinesAfterPrint":0}"""));
        var first = await printer.NextJobAsync();
        var row = Assert.Single(await app.JournalRowsAsync());
        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{row.Id}/reprint");
        var second = await printer.NextJobAsync();

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal([.. Prelude, .. OkBlock, .. FullCut], first);
        Assert.Equal(first, second);
    }

    private const string StoredText =
        """{"type":"Text","content":"ok","alignment":"Center","style":null,"size":null,"lines":1,"partialCut":false,"separatorChar":"=","separatorLength":32}""";
    private const string StoredLineFeed =
        """{"type":"LineFeed","content":null,"alignment":"Center","style":null,"size":null,"lines":3,"partialCut":false,"separatorChar":"=","separatorLength":32}""";
    private const string StoredCut =
        """{"type":"Cut","content":null,"alignment":"Center","style":null,"size":null,"lines":1,"partialCut":false,"separatorChar":"=","separatorLength":32}""";

    // A row from before the change holds the number that the model had then: 3 when the caller sent an options object
    // without the field. A reprint reads it as 3 lines (accepted by the owner).
    // A row with no feed field follows the rule of today: one that ends with a LineFeed block of 3 lines (the house style) keeps its bytes,
    // one that ends with text now gets 3 lines (accepted by the owner).
    [Theory]
    [InlineData("""{"codePage":null,"defaultLineSpacing":null,"autoCut":true,"feedLinesAfterPrint":3}""", false, 3)]
    [InlineData(null, false, 3)]
    [InlineData("""{"codePage":null,"defaultLineSpacing":null,"autoCut":true,"feedLinesAfterPrint":null}""", false, 3)]
    [InlineData(null, true, 0)]
    [InlineData("""{"codePage":null,"defaultLineSpacing":null,"autoCut":true,"feedLinesAfterPrint":null}""", true, 0)]
    public async Task Reprint_RowStoredBeforeTheChange_FollowsTheRuleOfToday(string? storedOptions, bool endsLikeTheHouseStyle, int lines)
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
                BlockCount = endsLikeTheHouseStyle ? 3 : 1,
                Payload = new PrintJobPayload
                {
                    JobId = id,
                    Headers = "{}",
                    Blocks = endsLikeTheHouseStyle ? $"[{StoredText},{StoredLineFeed},{StoredCut}]" : $"[{StoredText}]",
                    Options = storedOptions
                }
            });
            await db.SaveChangesAsync();
        }

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{id}/reprint");
        var reprinted = await printer.NextJobAsync();

        Assert.True(status == HttpStatusCode.OK, body);
        // The house style: the bytes of before, LF LF LF, ESC a 1, GS V 65 3.
        byte[] expected = endsLikeTheHouseStyle
            ? [.. Prelude, .. OkBlock, .. Center, .. Feed(3), .. Center, .. FullCut]
            : [.. Prelude, .. OkBlock, .. Feed(lines), .. FullCut];
        Assert.Equal(expected, reprinted);
    }

    [Fact]
    public async Task McpPrint_FeedField_FeedsLinesLikeHttp()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();

        var (isError, text) = await client.CallToolAsync("print", """{"content":[{"type":"Text","content":"ok"}],"options":{"feedLinesAfterPrint":5}}""");
        var (_, plainText) = await client.CallToolAsync("print", """{"content":[{"type":"Text","content":"ok"}]}""");
        var (_, nullText) = await client.CallToolAsync("print", """{"content":[{"type":"Text","content":"ok"}],"options":{"feedLinesAfterPrint":null}}""");
        var (_, zeroText) = await client.CallToolAsync("print", """{"content":[{"type":"Text","content":"ok"}],"options":{"feedLinesAfterPrint":0}}""");
        var (_, lineFeedText) = await client.CallToolAsync("print", """{"content":[{"type":"Text","content":"ok"},{"type":"LineFeed","lines":3}]}""");
        var rows = await app.JournalRowsAsync();

        Assert.False(isError, text);
        Assert.All([text, plainText, nullText, zeroText, lineFeedText], answer => Assert.Equal("Printed.", answer));
        Assert.Equal(5, rows.Count);
        Assert.Single(rows, row => row.Payload.Bytes!.AsSpan().SequenceEqual([.. Prelude, .. OkBlock, .. Feed(5), .. FullCut]));
        Assert.Equal(2, rows.Count(row => row.Payload.Bytes!.AsSpan().SequenceEqual(JobWithDefaultFeed)));
        Assert.Single(rows, row => row.Payload.Bytes!.AsSpan().SequenceEqual([.. Prelude, .. OkBlock, .. FullCut]));
        Assert.Single(rows, row => row.Payload.Bytes!.AsSpan().SequenceEqual([.. Prelude, .. OkBlock, .. Center, .. Feed(3), .. FullCut]));
    }

    // The number in the caller texts is the default feed, which is the feed of the house style.
    [Fact]
    public void CallerTexts_FeedBeforeACut_NameTheDefaultLines()
    {
        Assert.Contains($"The server keeps {CutFeed.DefaultLines} empty lines before each cut", PrinterTools.CutFeedRule);
        Assert.Contains($"count toward the {CutFeed.DefaultLines}, so add no LineFeed block for the cut", PrinterTools.CutFeedRule);
        Assert.Contains("0 adds none", PrinterTools.CutFeedRule);
        Assert.DoesNotContain("not both", PrinterTools.CutFeedRule);
        Assert.Contains(PrinterTools.CutFeedRule, PrinterTools.ServerInstructions);
        var description = typeof(PrintOptions).GetProperty(nameof(PrintOptions.FeedLinesAfterPrint))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        Assert.Contains($"Left out: the server keeps {CutFeed.DefaultLines} empty lines before the cut", description);
        Assert.Contains($"count toward the {CutFeed.DefaultLines}.", description);
        Assert.Contains("0 adds no line", description);
    }

    // The print tool description holds the rule too.
    [Fact]
    public async Task ToolsList_PrintDescription_HoldsTheCutFeedRule()
    {
        await using var app = new NoPrinterApp();

        var tools = (await app.CreateClient().McpAsync("tools/list")).GetProperty("tools");
        var print = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == PrinterTools.PrintName);

        Assert.Contains(PrinterTools.CutFeedRule, print.GetProperty("description").GetString());
    }
}
