using System.Net;
using System.Text.Json;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;
using static ThermalPrinterWeb.Tests.Support.TestBlocks;

namespace ThermalPrinterWeb.Tests;

// The house style of simple mode and print_note (#43, #47): Font B at 2 x 3 for the body,
// a word wrap on the server, a date line, enough feed before the cut.
public sealed class SimpleNoteTests
{
    private const string PrintUrl = TestHttp.PrintUrl;
    private const string DateLine = "2026-10-04";

    private static readonly DateOnly Date = new(2026, 10, 4);

    // Noon UTC: the same date in Poland.
    private static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    // ESC a n
    private static readonly byte[] Center = [0x1B, 0x61, 1];
    private static readonly byte[] Right = [0x1B, 0x61, 2];

    // ESC ! n: bit 0 is Font B, bit 3 is bold.
    private static readonly byte[] Plain = [0x1B, 0x21, 0x00];
    private static readonly byte[] FontB = [0x1B, 0x21, 0x01];
    private static readonly byte[] Bold = [0x1B, 0x21, 0x08];

    // GS ! n: width 2 in the high four bits, height 3 in the low four.
    private static readonly byte[] TwoByThree = [0x1D, 0x21, 0x12];
    private static readonly byte[] SizeOff = [0x1D, 0x21, 0x00];

    private static readonly byte[] Rule = [.. Center, .. Plain, .. Enumerable.Repeat((byte)'=', 48), 0x0A, .. Plain];

    // Three empty lines, then GS V 65 3.
    private static readonly byte[] FeedAndCut = [.. Center, 0x0A, 0x0A, 0x0A, .. Center, 0x1D, 0x56, 0x41, 0x03];

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // The real PrinterService with no printer, and a clock that does not move.
    private static NoPrinterApp NewApp() => new(new FixedClock(Noon));

    private static byte[] HeaderBytes(string text) => [.. Center, .. Bold, .. TwoByThree, .. Pc852Bytes(text), 0x0A, .. SizeOff, .. Plain];

    private static byte[] BodyBytes(string text, byte[]? alignment = null)
        => [.. alignment ?? Center, .. FontB, .. TwoByThree, .. Pc852Bytes(text), 0x0A, .. SizeOff, .. Plain];

    private static string[] TextsOf(List<PrintContent> note)
        => [.. note.Where(block => block.Type == ContentType.Text).Select(block => block.Content!)];

    private static string Lines(int count) => string.Join('\n', Enumerable.Repeat("x", count));

    // The paper of a note with a one-line title, in dots.
    private static int PaperDots(int bodyLines)
    {
        var tall = PaperLength.LineDots(null, SimpleNote.Height);
        var plain = PaperLength.LineDots(null);
        return tall + plain + bodyLines * tall + plain + tall + SimpleNote.FeedLines * plain + PaperLength.CutDots(3);
    }

    [Fact]
    public void Build_TitleAndMessage_IsTheHouseStyleBlockByBlock()
    {
        var note = SimpleNote.Build("Title", "Message", Date);

        Assert.Equal(
            [ContentType.Text, ContentType.Separator, ContentType.Text, ContentType.Separator, ContentType.Text, ContentType.LineFeed, ContentType.Cut],
            note.Select(block => block.Type));

        var header = note[0];
        Assert.Equal("Title", header.Content);
        Assert.Equal(Alignment.Center, header.Alignment);
        Assert.Equal([PrintStyle.Bold], header.Style);
        Assert.Equal((2, 3), (header.Size!.Width, header.Size.Height));

        var body = note[2];
        Assert.Equal("Message", body.Content);
        Assert.Equal(Alignment.Center, body.Alignment);
        Assert.Equal([PrintStyle.FontB], body.Style);
        Assert.Equal((2, 3), (body.Size!.Width, body.Size.Height));

        var signature = note[4];
        Assert.Equal(DateLine, signature.Content);
        Assert.Equal(Alignment.Right, signature.Alignment);
        Assert.Equal([PrintStyle.FontB], signature.Style);
        Assert.Equal((2, 3), (signature.Size!.Width, signature.Size.Height));

        Assert.All([note[1], note[3]], separator =>
        {
            Assert.Equal("=", separator.SeparatorChar);
            Assert.Equal(48, separator.SeparatorLength);
            Assert.Null(separator.Style);
            Assert.Null(separator.Size);
        });
        Assert.Equal(3, note[5].Lines);
    }

    // The numbers the MCP texts and CLAUDE.md state.
    [Fact]
    public void Columns_OfTheStyle_AreTwentyFourThirtyTwoAndAFullRule()
    {
        Assert.Equal(24, SimpleNote.HeaderColumns);
        Assert.Equal(32, SimpleNote.BodyColumns);
        // A rule in Font A at 1 x 1 is as wide as a full body line: one line, no wrap.
        Assert.Equal(48, SimpleNote.SeparatorLength);
        Assert.True(SimpleNote.SeparatorLength <= SeparatorBlockHandler.MaxLength);
    }

    // Font, size and alignment of every line, as the printer gets them.
    [Fact]
    public async Task JobBytes_Note_HaveTheFontSizeAndAlignmentOfEachLine()
    {
        const string Body = "Zażółć gęślą jaźń";

        var bytes = await JobBytesAsync(SimpleNote.Build("Styl domowy", Body, Date));

        Assert.Equal(
            [.. Prelude, .. HeaderBytes("Styl domowy"), .. Rule, .. BodyBytes(Body), .. Rule, .. BodyBytes(DateLine, Right), .. FeedAndCut],
            bytes);
    }

    [Fact]
    public async Task JobBytes_NoteWithAnImage_HaveTheImageUnderTheMessage()
    {
        var note = SimpleNote.Build("T", "M", Date, TestImages.PngBase64());

        Assert.Equal(
            [ContentType.Text, ContentType.Separator, ContentType.Text, ContentType.Separator, ContentType.Image, ContentType.Separator, ContentType.Text, ContentType.LineFeed, ContentType.Cut],
            note.Select(block => block.Type));
        Assert.Equal(DateLine, note[6].Content);
        Assert.NotEmpty(await JobBytesAsync(note));
    }

    [Theory]
    // A line that fits is not changed: spaces stay.
    [InlineData("", 10, "")]
    [InlineData("short", 10, "short")]
    [InlineData("exactly 10", 10, "exactly 10")]
    [InlineData("  two  spaces ", 16, "  two  spaces ")]
    // The break is at the last space that fits; the space is not printed.
    [InlineData("one two three", 10, "one two\nthree")]
    [InlineData("exactly 10 x", 10, "exactly 10\nx")]
    [InlineData("aaa bbb ccc ddd", 7, "aaa bbb\nccc ddd")]
    [InlineData("aaa   bbbbb", 7, "aaa\nbbbbb")]
    [InlineData("aaaa bbb      ", 8, "aaaa bbb")]
    // A word longer than the line breaks at the limit.
    [InlineData("abcdefghijkl", 5, "abcde\nfghij\nkl")]
    [InlineData("ab abcdefghijkl cd", 5, "ab\nabcde\nfghij\nkl cd")]
    // The line breaks of the caller stay, empty lines too.
    [InlineData("a\n\nb", 10, "a\n\nb")]
    [InlineData("one two three\nfour", 10, "one two\nthree\nfour")]
    [InlineData("a\r\nb\rc", 10, "a\nb\nc")]
    [InlineData("a\n", 10, "a\n")]
    // Polish letters are one column each.
    [InlineData("zażółć gęślą jaźń", 12, "zażółć gęślą\njaźń")]
    // An arrow prints as "->" and an emoji as "??": two columns each.
    [InlineData("abcd → ef", 7, "abcd →\nef")]
    [InlineData("abcde → ef", 7, "abcde\n→ ef")]
    [InlineData("ab 😀 cd", 8, "ab 😀 cd")]
    [InlineData("ab 😀 cd", 7, "ab 😀\ncd")]
    public void Wrap_Text_BreaksAtSpacesAndKeepsTheLineBreaks(string text, int columns, string expected)
    {
        Assert.Equal(expected, SimpleNote.Wrap(text, columns));
    }

    [Fact]
    public void Build_PolishProse_WrapsTheBodyAtThirtyTwoColumns()
    {
        const string Prose = "Zażółć gęślą jaźń, a potem sprawdź, czy drukarka łamie wiersze na spacjach. "
            + "Bardzo długie słowo Konstantynopolitańczykowianeczkówna też musi się zmieścić.";

        var body = TextsOf(SimpleNote.Build("Styl domowy", Prose, Date))[1];

        Assert.Equal(
            "Zażółć gęślą jaźń, a potem\n"
            + "sprawdź, czy drukarka łamie\n"
            + "wiersze na spacjach. Bardzo\n"
            + "długie słowo\n"
            + "Konstantynopolitańczykowianeczkó\n"
            + "wna też musi się zmieścić.",
            body);
        Assert.All(body.Split('\n'), line => Assert.InRange(line.Length, 1, SimpleNote.BodyColumns));
    }

    [Fact]
    public void Build_LongTitle_WrapsAtTwentyFourColumnsAndKeepsItsSize()
    {
        var header = SimpleNote.Build("Bardzo długi tytuł notatki do druku", "M", Date)[0];

        Assert.Equal("Bardzo długi tytuł\nnotatki do druku", header.Content);
        Assert.Equal((2, 3), (header.Size!.Width, header.Size.Height));
        Assert.Equal("A title of 24 characters", SimpleNote.Build("A title of 24 characters", "M", Date)[0].Content);
    }

    [Theory]
    // Summer: UTC + 2. Winter: UTC + 1.
    [InlineData("2026-10-04T21:59:00Z", "2026-10-04")]
    [InlineData("2026-10-04T22:00:00Z", "2026-10-05")]
    [InlineData("2026-12-31T22:59:00Z", "2026-12-31")]
    [InlineData("2026-12-31T23:00:00Z", "2027-01-01")]
    public void Today_AnyUtcTime_IsTheDateInPoland(string utc, string expected)
    {
        var clock = new FixedClock(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(DateOnly.ParseExact(expected, "yyyy-MM-dd"), SimpleNote.Today(clock));
    }

    // One word that fills the Text block after the wrap: 303 lines, 3 m. Each hard break adds one character.
    [Fact]
    public async Task PrintAsync_OneWordAtTheTextLimit_Prints()
    {
        var message = new string('x', TextBlockHandler.MaxLength - (TextBlockHandler.MaxLength - 1) / SimpleNote.BodyColumns);
        var note = SimpleNote.Build("T", message, Date);

        var result = await NewService().PrintAsync(note);

        Assert.Equal(PrintResult.Ok, result);
        Assert.InRange(note[2].Content!.Length, TextBlockHandler.MaxLength - SimpleNote.BodyColumns, TextBlockHandler.MaxLength);
        Assert.All(note[2].Content!.Split('\n'), line => Assert.InRange(line.Length, 1, SimpleNote.BodyColumns));
    }

    // 4 m of paper holds a fixed number of body lines; one more line is a validation failure.
    [Fact]
    public async Task PrintAsync_BodyLines_AreLimitedByThePaper()
    {
        var most = (PaperLength.MaxDots - PaperDots(0)) / PaperLength.LineDots(null, SimpleNote.Height);

        var fits = await NewService().PrintAsync(SimpleNote.Build("T", Lines(most), Date));
        var over = await NewService().PrintAsync(SimpleNote.Build("T", Lines(most + 1), Date));

        Assert.Equal(410, most);
        Assert.Equal(PrintResult.Ok, fits);
        Assert.Equal(PrintFailure.Validation, over.Failure);
        Assert.EndsWith($"the document is over the limit of {PaperLength.MaxDots} dots of paper ({PaperLength.MaxDots / PaperLength.DotsPerMetre} m)", over.Error);
    }

    // Short words give more lines than the paper or the block holds: the limits of a Text block answer.
    [Fact]
    public async Task PrintAsync_MessageOverTheTextLimits_IsAValidationFailure()
    {
        var shortWords = string.Join(' ', Enumerable.Repeat(new string('x', 17), 555));
        var tooLong = new string('x', TextBlockHandler.MaxLength + 1);

        var manyLines = await NewService().PrintAsync(SimpleNote.Build("T", shortWords, Date));
        var manyCharacters = await NewService().PrintAsync(SimpleNote.Build("T", tooLong, Date));

        Assert.True(shortWords.Length <= TextBlockHandler.MaxLength);
        Assert.Equal(PrintResult.Invalid($"Block 2 (Text): text line count 555 is over the limit of {TextBlockHandler.MaxLines}"), manyLines);
        Assert.Equal(PrintFailure.Validation, manyCharacters.Failure);
        Assert.StartsWith("Block 2 (Text): text length ", manyCharacters.Error);
    }

    // HTTP simple mode through the real pipeline: the bytes, the paper estimate and the journal row.
    [Fact]
    public async Task PostPrinter_SimpleMode_PrintsTheHouseStyleAndStoresIt()
    {
        await using var app = NewApp();
        var json = JsonSerializer.Serialize(new { name = "Styl domowy", message = "Zażółć gęślą jaźń i jeszcze kilka słów, żeby wiersz był za długi.", source = "test" });

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        var job = Assert.Single(await app.JournalRowsAsync());

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(
            [
                .. Prelude, .. HeaderBytes("Styl domowy"), .. Rule,
                .. BodyBytes("Zażółć gęślą jaźń i jeszcze\nkilka słów, żeby wiersz był za\ndługi."),
                .. Rule, .. BodyBytes(DateLine, Right), .. FeedAndCut
            ],
            job.Payload.Bytes!);
        Assert.Equal(PaperDots(bodyLines: 3), job.PaperDots);
        // The tray shows the title; the search reads the text as it is on the paper.
        Assert.Equal("Styl domowy", job.Title);
        Assert.Equal($"Styl domowy\nZażółć gęślą jaźń i jeszcze\nkilka słów, żeby wiersz był za\ndługi.\n{DateLine}", job.Payload.PlainText);
        Assert.Contains("\"style\":[\"FontB\"],\"size\":{\"width\":2,\"height\":3}", job.Payload.Blocks);
    }

    // print_note and HTTP simple mode print the same strip, and a reprint prints it again.
    [Fact]
    public async Task PrintNote_SameTitleAndMessage_PrintsTheBytesOfSimpleModeAndReprintsThem()
    {
        await using var app = NewApp();
        var client = app.CreateClient();
        var picture = TestImages.PngBase64();

        await client.SendJsonAsync(HttpMethod.Post, PrintUrl, JsonSerializer.Serialize(new { name = "Tytuł", message = "Treść notatki", imageBase64 = picture }));
        var (isError, text) = await client.CallToolAsync("print_note", JsonSerializer.Serialize(new { title = "Tytuł", message = "Treść notatki", imageBase64 = picture }));
        var rows = await app.JournalRowsAsync();
        var (reprintStatus, _) = await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{rows[0].Id}/reprint");
        var reprint = (await app.JournalRowsAsync())[^1];

        Assert.False(isError);
        Assert.Equal("Printed.", text);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(JobResult.Printed, row.Result));
        Assert.Equal(rows[0].Payload.Bytes, rows[1].Payload.Bytes);
        Assert.Equal(rows[0].Payload.Blocks, rows[1].Payload.Blocks);
        Assert.Equal(HttpStatusCode.OK, reprintStatus);
        Assert.Equal(rows[0].ByteCount, reprint.ByteCount);
        Assert.Equal(rows[0].PaperDots, reprint.PaperDots);
    }

    [Fact]
    public async Task PostPrinter_SimpleModeOverThePaperLimit_Answers400()
    {
        await using var app = NewApp();
        var json = JsonSerializer.Serialize(new { name = "T", message = Lines(450) });

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var response = JsonDocument.Parse(body).RootElement;
        Assert.Equal(PayloadErrorTests.OverPaper(2, ContentType.Text), response.GetProperty("error").GetString());
        Assert.Equal("validation", response.GetProperty("type").GetString());
    }

    // The numbers in the MCP texts are typed by hand: an attribute cannot read the constants.
    [Fact]
    public async Task ToolsList_PrintNote_StatesTheColumnsAndTheLimitsOfTheStyle()
    {
        await using var app = NewApp();
        var tools = (await app.CreateClient().McpAsync("tools/list")).GetProperty("tools");
        var tool = tools.EnumerateArray().Single(candidate => candidate.GetProperty("name").GetString() == "print_note");
        var description = tool.GetProperty("description").GetString();
        var arguments = tool.GetProperty("inputSchema").GetProperty("properties");
        string Argument(string name) => arguments.GetProperty(name).GetProperty("description").GetString()!;
        var size = $"size {SimpleNote.Width}x{SimpleNote.Height}";

        Assert.Contains($"The title holds {SimpleNote.HeaderColumns} characters per line, the message {SimpleNote.BodyColumns}.", description);
        Assert.Contains("The server breaks a longer line at a space", description);
        Assert.Contains($"at most {TextBlockHandler.MaxLength} characters and about 400 printed lines ({PaperLength.MaxDots / PaperLength.DotsPerMetre} m of paper)", description);
        Assert.Contains($"({size})", Argument("title"));
        Assert.Contains($"{SimpleNote.HeaderColumns} characters per line", Argument("title"));
        Assert.Contains($"(FontB at {size})", Argument("message"));
        Assert.Contains($"{SimpleNote.BodyColumns} characters per line", Argument("message"));

        Assert.Contains(
            $"House style: headline Bold with {size} ({SimpleNote.HeaderColumns} characters per line), body FontB with {size} ({SimpleNote.BodyColumns} characters per line), "
            + $"a {SimpleNote.SeparatorLength}-character Separator between them",
            PrinterTools.ServerInstructions);
    }
}
