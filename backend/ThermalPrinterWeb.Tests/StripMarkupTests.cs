using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Controllers;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;
using static ThermalPrinterWeb.Tests.Support.TestBlocks;

namespace ThermalPrinterWeb.Tests;

// Text mode (#53): plain text with line markers, compiled to the blocks of the house style.
public sealed class StripMarkupTests
{
    private const string PrintUrl = TestHttp.PrintUrl;

    // A strip as a caller writes it: headline, rule, prose, steps, QR code.
    private const string Strip =
        "# PR CZEKA\n"
        + "===\n"
        + "Zażółć gęślą jaźń i jeszcze kilka słów, żeby wiersz był za długi.\n"
        + "\n"
        + "[ ] Otwórz pokrywę drukarki i sprawdź, czy papier jest założony prosto.\n"
        + "[ ] Zamknij pokrywę.\n"
        + "qr: https://github.com/o/r/pull/1\n"
        + "===\n";

    // The same strip in template mode.
    private static List<PrintContent> StripBlocks() =>
    [
        Styled("PR CZEKA", Alignment.Center, PrintStyle.Bold),
        Rule("="),
        Styled("Zażółć gęślą jaźń i jeszcze\nkilka słów, żeby wiersz był za\ndługi.", Alignment.Center, PrintStyle.FontB),
        new() { Type = ContentType.LineFeed, Lines = 1 },
        Styled("[ ] Otwórz pokrywę drukarki i\n    sprawdź, czy papier jest\n    założony prosto.", Alignment.Left, PrintStyle.FontB),
        Styled("[ ] Zamknij pokrywę.", Alignment.Left, PrintStyle.FontB),
        new() { Type = ContentType.QRCode, Content = "https://github.com/o/r/pull/1" },
        Rule("=")
    ];

    private static PrintContent Styled(string text, Alignment alignment, params PrintStyle[] styles) => new()
    {
        Type = ContentType.Text,
        Content = text,
        Alignment = alignment,
        Style = [.. styles],
        Size = new TextSize { Width = 2, Height = 3 }
    };

    private static PrintContent Rule(string character)
        => new() { Type = ContentType.Separator, SeparatorChar = character, SeparatorLength = 48 };

    // One block as one line of text: the fields that text mode sets.
    private static string Describe(PrintContent block) => block.Type switch
    {
        ContentType.Text => $"Text {block.Alignment} {string.Join('+', block.Style!)} {block.Size!.Width}x{block.Size.Height} <{block.Content}>",
        ContentType.Separator => $"Separator {block.Alignment} {block.SeparatorChar} {block.SeparatorLength}",
        ContentType.LineFeed => $"LineFeed {block.Lines}",
        ContentType.QRCode => $"QRCode {block.Alignment} <{block.Content}>",
        _ => $"{block.Type}"
    };

    private static string[] Compiled(string text, Alignment align = Alignment.Center)
        => [.. StripMarkup.Compile(text, align).Select(Describe)];

    private static string Body(string text) => $"Text Center FontB 2x3 <{text}>";

    private static string Left(string text) => $"Text Left FontB 2x3 <{text}>";

    private static string Json(object body) => JsonSerializer.Serialize(body);

    public static TheoryData<string, string[]> Markers => new()
    {
        // Headline: the title line of the house style.
        { "# PAPERCUT", ["Text Center Bold 2x3 <PAPERCUT>"] },
        { "#   Tytuł", ["Text Center Bold 2x3 <Tytuł>"] },
        { "# Bardzo długi tytuł notatki do druku", ["Text Center Bold 2x3 <Bardzo długi tytuł\nnotatki do druku>"] },
        // Bold line, two spellings.
        { "## Ważne", ["Text Center FontB+Bold 2x3 <Ważne>"] },
        { "**Ważne**", ["Text Center FontB+Bold 2x3 <Ważne>"] },
        { "** Ważne **", ["Text Center FontB+Bold 2x3 <Ważne>"] },
        // Reversed line.
        { "==UWAGA==", ["Text Center FontB+ReverseMode 2x3 <UWAGA>"] },
        { "== UWAGA ==", ["Text Center FontB+ReverseMode 2x3 <UWAGA>"] },
        // Rules: 3 or more of one character.
        { "===", ["Separator Center = 48"] },
        { "==========================================================================", ["Separator Center = 48"] },
        { "---", ["Separator Center - 48"] },
        { "-----", ["Separator Center - 48"] },
        // QR code: the data as it is, no wrap.
        { "qr: https://github.com/wiktorkowalski/thermal-printer-web/pull/53", ["QRCode Center <https://github.com/wiktorkowalski/thermal-printer-web/pull/53>"] },
        { "QR:   dane z odstępem", ["QRCode Center <dane z odstępem>"] },
        // Empty lines. The line break at the end of the text adds no line.
        { "a\n\nb", [Body("a"), "LineFeed 1", Body("b")] },
        { "a\n \t\nb", [Body("a"), "LineFeed 1", Body("b")] },
        { "a\n", [Body("a")] },
        { "a\n\n", [Body("a"), "LineFeed 1"] },
        { "\n", ["LineFeed 1"] },
        { "", [] },
        // Every line ending of the encoder.
        { "a\r\nb\rc", [Body("a"), Body("b"), Body("c")] },
        { "a\fb\u0085c\u2028d\u2029e", [Body("a"), Body("b"), Body("c"), Body("d"), Body("e")] },
        // A last line of spaces with no line break after it is no line.
        { "a\n  ", [Body("a")] },
        { " \t ", [] },
        // Spaces at the end of a line are not printed.
        { "tekst   \t", [Body("tekst")] },
        // The escape: the rest of the line is body text.
        { "\\# nie nagłówek", [Body("# nie nagłówek")] },
        { "\\===", [Body("===")] },
        { "\\- nie lista", [Body("- nie lista")] },
        { "\\\\x", [Body("\\x")] },
        { "\\", ["LineFeed 1"] },
        // List items and indented lines: Left.
        { "- mleko", [Left("- mleko")] },
        { "* chleb", [Left("* chleb")] },
        { "1. pierwszy", [Left("1. pierwszy")] },
        { "12) dwunasty", [Left("12) dwunasty")] },
        { "999. ostatni", [Left("999. ostatni")] },
        { "[ ] krok", [Left("[ ] krok")] },
        { "[x] gotowe", [Left("[x] gotowe")] },
        { "[X] gotowe", [Left("[X] gotowe")] },
        { "- [ ] krok", [Left("- [ ] krok")] },
        { "- [x] gotowe", [Left("- [x] gotowe")] },
        { "1. [ ] krok", [Left("1. [ ] krok")] },
        { "1. [ ] aaaaaaaaaa bbbbbbbbbb cccccccccc", [Left("1. [ ] aaaaaaaaaa bbbbbbbbbb\n       cccccccccc")] },
        { "  - pod spodem", [Left("  - pod spodem")] },
        { "    wcięty wiersz", [Left("    wcięty wiersz")] },
        { "\twcięty tabulatorem", [Left(" wcięty tabulatorem")] },
        // The lines that the wrap adds start under the text of the item.
        {
            "[ ] Otwórz pokrywę drukarki i sprawdź, czy papier jest założony prosto.",
            [Left("[ ] Otwórz pokrywę drukarki i\n    sprawdź, czy papier jest\n    założony prosto.")]
        },
        { "10. aaaaaaaaaa bbbbbbbbbb cccccccccc dddddddddd", [Left("10. aaaaaaaaaa bbbbbbbbbb\n    cccccccccc dddddddddd")] },
        { "  aaaaaaaaaa bbbbbbbbbb cccccccccc dddddddddd", [Left("  aaaaaaaaaa bbbbbbbbbb\n  cccccccccc dddddddddd")] },
        // An indent over half of the line: no hanging indent.
        { "                  abc def", [Left("                  abc def")] },
        { "                  aaaaaaaaaa bbbbbbbbbb", [Left("                  aaaaaaaaaa\nbbbbbbbbbb")] },
        // A marker in the middle of a line, or one that is not complete, is plain text.
        { "#53 merged", [Body("#53 merged")] },
        { "#", [Body("#")] },
        { "##", [Body("##")] },
        { "### trzy", [Body("### trzy")] },
        { "==", [Body("==")] },
        { "==x", [Body("==x")] },
        { "**", [Body("**")] },
        { "****", [Body("****")] },
        { "**a** i **b** dalej", [Body("**a** i **b** dalej")] },
        // A fence inside the line, or one more fence character at an end: not a whole-line fence.
        { "**a** i **b**", [Body("**a** i **b**")] },
        { "**a**b**", [Body("**a**b**")] },
        { "==a== b ==c==", [Body("==a== b ==c==")] },
        { "=== TITLE ===", [Body("=== TITLE ===")] },
        { "***x***", [Body("***x***")] },
        // A marker ends with a space, not with a tab or another Unicode space.
        { "#\ttytuł", [Body("#\ttytuł")] },
        { "qr:\tdane", [Body("qr:\tdane")] },
        { "- ", [Body("-")] },
        { "-- x", [Body("-- x")] },
        { "-x", [Body("-x")] },
        { "= = =", [Body("= = =")] },
        { "=-=", [Body("=-=")] },
        { "qr:", [Body("qr:")] },
        { "qr:x", [Body("qr:x")] },
        { "1.5 l mleka", [Body("1.5 l mleka")] },
        { "2026. rok", [Body("2026. rok")] },
        { "[y] x", [Body("[y] x")] },
        { "a # b", [Body("a # b")] },
        { "2026-10-07 * Claude", [Body("2026-10-07 * Claude")] },
        // Body text wraps at 32 columns.
        {
            "Zażółć gęślą jaźń, a potem sprawdź, czy drukarka łamie wiersze na spacjach.",
            [Body("Zażółć gęślą jaźń, a potem\nsprawdź, czy drukarka łamie\nwiersze na spacjach.")]
        }
    };

    [Theory]
    [MemberData(nameof(Markers))]
    public void Compile_OneConstruct_IsTheBlockOfTheHouseStyle(string text, string[] expected)
    {
        Assert.Equal(expected, Compiled(text));
    }

    [Fact]
    public void Compile_AStrip_IsOneBlockPerLine()
    {
        var blocks = StripMarkup.Compile(Strip);

        Assert.Equal(StripBlocks().Select(Describe), blocks.Select(Describe));
        // Block N is line N + 1 of the text.
        Assert.Equal(Strip.TrimEnd('\n').Split('\n').Length, blocks.Count);
        // The style is the one of simple mode: no second copy of a number.
        Assert.Equal((SimpleNote.Width, SimpleNote.Height), (blocks[0].Size!.Width, blocks[0].Size!.Height));
        Assert.Equal(SimpleNote.SeparatorLength, blocks[1].SeparatorLength);
        Assert.All(blocks[2].Content!.Split('\n'), line => Assert.InRange(line.Length, 1, SimpleNote.BodyColumns));
    }

    // "align" moves the body lines only.
    [Theory]
    [InlineData(Alignment.Left)]
    [InlineData(Alignment.Right)]
    public void Compile_WithAnAlignment_MovesTheBodyLinesOnly(Alignment align)
    {
        var blocks = Compiled("# T\nbody\n## bold\n==reverse==\n\\escaped\n- item\n  indented\n===\nqr: data", align);

        Assert.Equal(
            [
                "Text Center Bold 2x3 <T>",
                $"Text {align} FontB 2x3 <body>",
                $"Text {align} FontB+Bold 2x3 <bold>",
                $"Text {align} FontB+ReverseMode 2x3 <reverse>",
                $"Text {align} FontB 2x3 <escaped>",
                Left("- item"),
                Left("  indented"),
                "Separator Center = 48",
                "QRCode Center <data>"
            ],
            blocks);
    }

    [Theory]
    [InlineData(null, Alignment.Center)]
    [InlineData("", Alignment.Center)]
    [InlineData("left", Alignment.Left)]
    [InlineData("Left", Alignment.Left)]
    [InlineData("CENTER", Alignment.Center)]
    [InlineData("right", Alignment.Right)]
    public void TryParseAlign_AName_IsTheAlignment(string? align, Alignment expected)
    {
        Assert.True(StripMarkup.TryParseAlign(align, out var alignment));
        Assert.Equal(expected, alignment);
    }

    [Theory]
    [InlineData("middle")]
    [InlineData(" left")]
    [InlineData("0")]
    [InlineData("99")]
    public void TryParseAlign_AnyOtherText_IsNotAnAlignment(string align)
    {
        Assert.False(StripMarkup.TryParseAlign(align, out _));
    }

    [Theory]
    // The word before the punctuation goes to the new line with it.
    [InlineData("aaaa bbbb - cc", 10, "aaaa\nbbbb - cc")]
    [InlineData("aaaa bbbb → cc", 10, "aaaa\nbbbb → cc")]
    [InlineData("aaaa bbbb -> cc", 10, "aaaa\nbbbb -> cc")]
    // The punctuation fits: no change.
    [InlineData("aaa bbb - cc", 10, "aaa bbb -\ncc")]
    // No word to take along, or the two do not fit in one line: the break of simple mode.
    [InlineData("aaaaaaaaaa - b", 10, "aaaaaaaaaa\n- b")]
    [InlineData("aa bbbbbbbbb - c", 10, "aa\nbbbbbbbbb\n- c")]
    // Three characters are a word, and a letter is no punctuation.
    [InlineData("aaaa bbbb ... c", 10, "aaaa bbbb\n... c")]
    [InlineData("aaaa bbbb i cc", 10, "aaaa bbbb\ni cc")]
    // The exception: the word before is short punctuation too. It goes to the new line and starts it.
    [InlineData("aaaaa b - -", 9, "aaaaa b\n- -")]
    public void Wrap_KeepPunctuation_DoesNotStartALineWithShortPunctuation(string text, int columns, string expected)
    {
        Assert.Equal(expected, WordWrap.Wrap(text, columns, keepPunctuation: true));
    }

    // The flag is off for simple mode and "wrap": true: their line breaks do not change.
    [Fact]
    public void Wrap_WithoutTheFlag_BreaksAsBefore()
    {
        Assert.Equal("aaaa bbbb\n- cc", WordWrap.Wrap("aaaa bbbb - cc", 10));
        Assert.Equal("aaaa bbbb\n→ cc", WordWrap.Wrap("aaaa bbbb → cc", 10));
    }

    // The most work of the compile: a text at the limit. One pass, and the blocks hold at most twice the text.
    [Theory]
    [InlineData("x")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("x\n")]
    [InlineData("# x\n")]
    [InlineData("- x - \n")]
    [InlineData("\\\n")]
    [InlineData("**\n")]
    [InlineData("==")]
    [InlineData("😀")]
    [InlineData("a - ")]
    [InlineData("[ ] ")]
    [InlineData("\t")]
    [InlineData("1")]
    public async Task Compile_TextAtTheLimit_IsOnePassWithABoundedResult(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, StripMarkup.MaxLength / unit.Length));
        var clock = Stopwatch.StartNew();

        var blocks = StripMarkup.Compile(text);
        var result = await NewService().PrintAsync(blocks);

        Assert.InRange(text.Length, StripMarkup.MaxLength - unit.Length, StripMarkup.MaxLength);
        Assert.InRange(blocks.Count, 0, StripMarkup.MaxLength);
        Assert.InRange(blocks.Sum(block => block.Content?.Length ?? 0), 0, 2 * StripMarkup.MaxLength);
        // The limits of the print path answer: a result, never a fault of the service.
        Assert.True(result.Success || result.Failure == PrintFailure.Validation, result.Error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Took {clock.Elapsed}.");
    }

    // The request body limit lets 30 MB of text in. A text over the limit is not read: one block, rejected for its length.
    [Fact]
    public async Task Compile_ThirtyMegabytesOfText_IsRejectedWithNoWork()
    {
        var huge = string.Concat(Enumerable.Repeat("# ż\n", 3_750_000));
        var clock = Stopwatch.StartNew();

        var blocks = StripMarkup.Compile(huge);
        var result = await NewService().PrintAsync(blocks);

        Assert.Same(huge, Assert.Single(blocks).Content);
        Assert.Equal(PrintResult.Invalid($"Block 0 (Text): text length {huge.Length} is over the limit of {StripMarkup.MaxLength}"), result);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public async Task Compile_TextOneCharacterOverTheLimit_IsRejected()
    {
        // One word: each break of the wrap adds a character, and the Text block counts the text after the wrap.
        var atLimit = await NewService().PrintAsync(StripMarkup.Compile(new string('x', StripMarkup.MaxLength - 400)));
        var wrappedOver = await NewService().PrintAsync(StripMarkup.Compile(new string('x', StripMarkup.MaxLength)));
        var over = await NewService().PrintAsync(StripMarkup.Compile(new string('x', StripMarkup.MaxLength + 1)));

        Assert.Equal(TextBlockHandler.MaxLength, StripMarkup.MaxLength);
        Assert.Equal(PrintResult.Ok, atLimit);
        // The number is not the length that the caller sent: CLAUDE.md and the tool text say so.
        Assert.Equal(PrintResult.Invalid($"Block 0 (Text): text length 10312 is over the limit of {StripMarkup.MaxLength}"), wrappedOver);
        Assert.Equal(PrintResult.Invalid($"Block 0 (Text): text length {StripMarkup.MaxLength + 1} is over the limit of {StripMarkup.MaxLength}"), over);
    }

    // One line is one block: the block limit of a document is the line limit of a text.
    [Fact]
    public async Task PrintAsync_TextWithMoreLinesThanBlocks_IsAValidationFailure()
    {
        string Lines(int count) => string.Join('\n', Enumerable.Repeat("===", count));

        var fits = await NewService().PrintAsync(StripMarkup.Compile(Lines(PrinterService.MaxBlocks)));
        var over = await NewService().PrintAsync(StripMarkup.Compile(Lines(PrinterService.MaxBlocks + 1)));

        Assert.Equal(PrintResult.Ok, fits);
        Assert.Equal(PrintResult.Invalid($"block count {PrinterService.MaxBlocks + 1} is over the limit of {PrinterService.MaxBlocks}"), over);
    }

    // The compile stops one block over the limit: the journal row of the rejected job stays small.
    [Theory]
    [InlineData("\n")]
    [InlineData("a\n")]
    public async Task Compile_TextOfTenThousandLines_StopsOneBlockOverTheLimit(string line)
    {
        var blocks = StripMarkup.Compile(string.Concat(Enumerable.Repeat(line, StripMarkup.MaxLength / line.Length)));

        var result = await NewService().PrintAsync(blocks);

        Assert.Equal(PrinterService.MaxBlocks + 1, blocks.Count);
        Assert.Equal(PrintResult.Invalid($"block count {PrinterService.MaxBlocks + 1} is over the limit of {PrinterService.MaxBlocks}"), result);
    }

    // An error names a block, and the block number is the line number less one.
    [Fact]
    public async Task PrintAsync_ALineThatItsBlockRejects_NamesTheLine()
    {
        var text = "# T\n===\nqr: " + new string('x', 3000) + "\nbody";

        var result = await NewService().PrintAsync(StripMarkup.Compile(text));

        Assert.Equal(PrintFailure.Validation, result.Failure);
        Assert.StartsWith("Block 2 (QRCode): ", result.Error);
        Assert.DoesNotContain("xxxx", result.Error);
    }

    // No marker makes a block that sounds, lights, cuts or reads a picture.
    [Fact]
    public void Compile_AnyText_MakesOnlyTextSeparatorLineFeedAndQRCodeBlocks()
    {
        var text = string.Join('\n', Markers.Select(row => (string)row[0]))
            + "\nSignal\nsignal: Sound\nbeep: 3\ncut:\nimage: AAAA\nbarcode: 123\n{\"type\":\"Signal\"}\n![x](http://example.com/x.png)\n<img src=x>";

        var types = StripMarkup.Compile(text).Select(block => block.Type).Distinct();

        Assert.Equal([ContentType.LineFeed, ContentType.QRCode, ContentType.Separator, ContentType.Text], types.OrderBy(type => type.ToString()));
    }

    // HTTP through the real pipeline: the bytes of the same strip in template mode, and the journal row.
    [Fact]
    public async Task PostPrinter_Text_PrintsTheCompiledBlocksAndStoresThem()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, Json(new { text = Strip, source = "test" }));
        var job = Assert.Single(await app.JournalRowsAsync());

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(await JobBytesAsync(StripBlocks()), job.Payload.Bytes!);
        Assert.Equal(JobResult.Printed, job.Result);
        Assert.Equal(PrintJobLog.HttpTransport, job.Transport);
        Assert.Equal("test", job.Source);
        Assert.Equal("PR CZEKA", job.Title);
        Assert.Equal(StripBlocks().Count, job.BlockCount);
        // The row holds the text as it came and the blocks that it made; the search reads the text as it is on the paper.
        Assert.Contains("# PR CZEKA\\n===", System.Text.Encoding.UTF8.GetString(job.Payload.Request!));
        Assert.StartsWith("""[{"type":"Text","content":"PR CZEKA",""", job.Payload.Blocks);
        Assert.Contains("\"style\":[\"FontB\"],\"size\":{\"width\":2,\"height\":3}", job.Payload.Blocks);
        Assert.Contains("kilka słów, żeby wiersz był za\ndługi.", job.Payload.PlainText);
        Assert.Null(job.Payload.Options);
    }

    // A reprint sends the stored blocks: the same strip, with no second compile.
    [Fact]
    public async Task Reprint_OfATextJob_PrintsTheSameStrip()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        await client.SendJsonAsync(HttpMethod.Post, PrintUrl, Json(new { text = Strip }));
        var first = Assert.Single(await app.JournalRowsAsync());

        var (status, _) = await client.SendJsonAsync(HttpMethod.Post, $"{PrintUrl}/jobs/{first.Id}/reprint");
        var reprint = (await app.JournalRowsAsync())[^1];
        var (_, detail) = await client.SendJsonAsync(HttpMethod.Get, $"{PrintUrl}/jobs/{first.Id}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(first.Id, reprint.ReprintOf);
        Assert.Equal(first.ByteCount, reprint.ByteCount);
        Assert.Equal(first.PaperDots, reprint.PaperDots);
        // The job endpoint serves the blocks: the tray and the journal page need no change.
        Assert.Equal(StripBlocks().Count, JsonDocument.Parse(detail).RootElement.GetProperty("blocks").GetArrayLength());
    }

    // The ledger reads the compiled blocks: a strip in the markup with the header PAPERCUT is a papercut.
    [Fact]
    public async Task Papercuts_AStripInTheMarkup_IsInTheLedger()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        var first = Json(new { text = "# PAPERCUT\n===\nzsh zjada glob bez cudzysłowu.\n===\n" });
        // The subject on the headline is the first printed line of it: a headline holds 24 characters per line.
        var second = Json(new { text = "# PAPERCUT: wolny build\n- warstwa npm ci nie jest w cache" });

        Assert.Equal(HttpStatusCode.OK, (await client.SendJsonAsync(HttpMethod.Post, PrintUrl, first)).Status);
        Assert.Equal(HttpStatusCode.OK, (await client.SendJsonAsync(HttpMethod.Post, PrintUrl, second)).Status);
        var rows = await app.JournalRowsAsync();
        var (status, body) = await client.SendJsonAsync(HttpMethod.Get, $"{PrintUrl}/jobs/papercuts");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("PAPERCUT", rows[0].Title);
        var ledger = JsonDocument.Parse(body).RootElement;
        Assert.Equal(2, ledger.GetProperty("strips").GetInt32());
        Assert.Equal(
            ["wolny build", "zsh zjada glob bez cudzysłowu."],
            ledger.GetProperty("papercuts").EnumerateArray().Select(papercut => papercut.GetProperty("subject").GetString()).Order());
    }

    // The options of the request go to the print path as in template mode.
    [Fact]
    public async Task PostPrinter_TextWithOptions_UsesTheOptions()
    {
        await using var app = new NoPrinterApp();
        var json = Json(new { text = Strip, align = "left", options = new { autoCut = false } });

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        var job = Assert.Single(await app.JournalRowsAsync());

        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(await JobBytesAsync(StripMarkup.Compile(Strip, Alignment.Left), new PrintOptions { AutoCut = false }), job.Payload.Bytes!);
        // The body line and the two steps.
        Assert.Equal(3, job.Payload.Blocks!.Split("\"alignment\":\"Left\"").Length - 1);
    }

    // options.sign (#54) works as in template mode: one line after the last line of the text.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Print_TextWithASign_AddsTheSignatureLineAfterTheText(bool overMcp)
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        var json = Json(new { text = "# T\nbody", options = new { sign = "Claude" } });

        if (overMcp)
            Assert.Equal("Printed.", (await client.CallToolAsync("print", json)).Text);
        else
            Assert.Equal(HttpStatusCode.OK, (await client.SendJsonAsync(HttpMethod.Post, PrintUrl, json)).Status);
        var job = Assert.Single(await app.JournalRowsAsync());

        var blocks = JsonDocument.Parse(job.Payload.Blocks!).RootElement;
        Assert.Equal(3, blocks.GetArrayLength());
        Assert.Equal("T", blocks[0].GetProperty("content").GetString());
        Assert.Equal("body", blocks[1].GetProperty("content").GetString());
        Assert.EndsWith(" * Claude", blocks[2].GetProperty("content").GetString());
        Assert.Equal("Right", blocks[2].GetProperty("alignment").GetString());
    }

    public static TheoryData<string, string> RejectedRequests => new()
    {
        { Json(new { text = Secret, content = new object[] { new { type = "Text", content = "x" } } }), StripMarkup.ConflictError },
        { Json(new { text = Secret, name = "n" }), StripMarkup.ConflictError },
        { Json(new { text = Secret, message = "m" }), StripMarkup.ConflictError },
        { Json(new { text = Secret, name = "n", message = "m" }), StripMarkup.ConflictError },
        { Json(new { text = Secret, imageBase64 = "AAAA" }), StripMarkup.ConflictError },
        { Json(new { text = "x", align = Secret }), StripMarkup.AlignError },
        { Json(new { text = "x", align = "99" }), StripMarkup.AlignError },
        // An empty text is no text.
        { Json(new { text = "" }), PrinterController.NoJobError },
        { Json(new { text = "", align = "left" }), PrinterController.NoJobError },
        // Spaces only: no line to print, not an empty strip.
        { Json(new { text = " " }), PrinterController.NoJobError },
        { Json(new { text = " \t " }), PrinterController.NoJobError }
    };

    [Theory]
    [MemberData(nameof(RejectedRequests))]
    public async Task PostPrinter_TextWithAnotherModeOrAWrongAlign_Answers400(string json, string expectedError)
    {
        await using var app = new NoPrinterApp();

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        var job = Assert.Single(await app.JournalRowsAsync());

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var response = JsonDocument.Parse(body).RootElement;
        Assert.Equal(expectedError, response.GetProperty("error").GetString());
        Assert.Equal("validation", response.GetProperty("type").GetString());
        Assert.DoesNotContain(Secret, body);
        // A refused job: the row holds the request and no blocks.
        Assert.Equal(JobResult.Validation, job.Result);
        Assert.Null(job.Payload.Blocks);
        Assert.Null(job.Payload.Bytes);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
    }

    [Fact]
    public void Errors_OfTextMode_AreFixedTexts()
    {
        Assert.Equal("text cannot go with content, name, message or imageBase64: send text alone", StripMarkup.ConflictError);
        Assert.Equal("align must be Left, Center or Right", StripMarkup.AlignError);
        Assert.Equal("Request must have Content array, Text or both Name and Message", PrinterController.NoJobError);
    }

    // Template mode and simple mode do not read the new fields.
    [Fact]
    public async Task PostPrinter_EmptyTextWithContent_IsTemplateMode()
    {
        await using var app = new NoPrinterApp();
        var json = Json(new { text = "", align = "nonsense", content = new object[] { new { type = "Text", content = "x" } } });

        var (status, _) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        var job = Assert.Single(await app.JournalRowsAsync());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(await JobBytesAsync([new() { Type = ContentType.Text, Content = "x" }]), job.Payload.Bytes!);
    }

    [Fact]
    public async Task PostPrinter_TextOverTheLimit_Answers400WithTheLength()
    {
        await using var app = new NoPrinterApp();
        var json = Json(new { text = new string('x', StripMarkup.MaxLength + 1) });

        var (status, body) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(
            $"Block 0 (Text): text length {StripMarkup.MaxLength + 1} is over the limit of {StripMarkup.MaxLength}",
            JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        // The limit is logged where it applies, with numbers only.
        Assert.Single(app.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith("Rejected print: block 0 (Text)", StringComparison.Ordinal));
    }

    // The MCP print tool: the same compile, the same row.
    [Fact]
    public async Task PrintTool_Text_PrintsTheBlocksOfHttp()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();

        await client.SendJsonAsync(HttpMethod.Post, PrintUrl, Json(new { text = Strip, align = "Left" }));
        var (isError, answer) = await client.CallToolAsync("print", Json(new { text = Strip, align = "left", source = "claude-code" }));
        var rows = await app.JournalRowsAsync();

        Assert.False(isError, answer);
        Assert.Equal("Printed.", answer);
        Assert.Equal(2, rows.Count);
        Assert.Equal(PrintJobLog.McpTransport("print"), rows[1].Transport);
        Assert.Equal("claude-code", rows[1].Source);
        Assert.Equal(rows[0].Payload.Bytes, rows[1].Payload.Bytes);
        Assert.Equal(rows[0].Payload.Blocks, rows[1].Payload.Blocks);
    }

    // As over HTTP: an empty content array is no content.
    [Fact]
    public async Task PrintTool_TextWithAnEmptyContentArray_IsTextMode()
    {
        await using var app = new NoPrinterApp();

        var (isError, answer) = await app.CreateClient().CallToolAsync("print", """{"text":"# T","content":[]}""");
        var job = Assert.Single(await app.JournalRowsAsync());

        Assert.False(isError, answer);
        Assert.Equal("Printed.", answer);
        Assert.Equal("T", job.Title);
    }

    [Theory]
    [InlineData($$$"""{"text":"{{{Secret}}}","content":[{"type":"Text","content":"x"}]}""", "'content' and 'text' are both sent: send one of them")]
    [InlineData($$$"""{"text":"x","align":"{{{Secret}}}"}""", "'align' must be Left, Center or Right")]
    [InlineData("""{"text":""}""", "'content' or 'text' is missing")]
    [InlineData("""{"text":" \t "}""", "'content' or 'text' is missing")]
    [InlineData("""{"align":"left"}""", "'content' or 'text' is missing")]
    [InlineData($$$"""{"text":["{{{Secret}}}"]}""", "'text' has the wrong JSON type")]
    public async Task PrintTool_TextWithAWrongCall_AnswersWithTheCorrectShape(string arguments, string expectedProblem)
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        // The start of the host logs a Warning of its own.
        app.Logs.Entries.Clear();

        var (isError, answer) = await client.CallToolAsync("print", arguments);
        var job = Assert.Single(await app.JournalRowsAsync());

        Assert.True(isError);
        Assert.Equal($"Wrong arguments for 'print': {expectedProblem}. Example of a valid call: {PrinterTools.ValidCallFor("print")}", answer);
        Assert.Contains(PrinterTools.PrintTextExample, answer);
        Assert.DoesNotContain(Secret, answer);
        Assert.Equal(JobResult.Validation, job.Result);
        Assert.Null(job.Payload.Blocks);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
    }

    // The example follows the advice of the text: it prints, and no line is wider than its style allows.
    [Fact]
    public async Task PrintTool_TextExample_IsAValidCallInsideThePaper()
    {
        await using var app = new NoPrinterApp();
        var text = JsonDocument.Parse(PrinterTools.PrintTextExample).RootElement.GetProperty("text").GetString()!;

        var (isError, answer) = await app.CreateClient().CallToolAsync("print", PrinterTools.PrintTextExample);

        Assert.False(isError, answer);
        Assert.Equal("Printed.", answer);
        Assert.DoesNotContain("Signal", PrinterTools.PrintTextExample);
        // The example holds prose that the server breaks.
        Assert.Contains(StripMarkup.Compile(text), block => block.Content?.Contains('\n') == true);
        Assert.Equal("TITLE MAX 24 CHARS", StripMarkup.Compile(text)[0].Content);
        Assert.Contains(ContentType.QRCode, StripMarkup.Compile(text).Select(block => block.Type));
    }

    // The numbers in the MCP texts are typed by hand: an attribute cannot read the constants.
    [Fact]
    public async Task ToolsList_PrintTool_StatesTheMarkersAndTheLimitsOfTextMode()
    {
        await using var app = new NoPrinterApp();
        var tools = (await app.CreateClient().McpAsync("tools/list")).GetProperty("tools");
        var tool = tools.EnumerateArray().Single(candidate => candidate.GetProperty("name").GetString() == "print");
        var description = tool.GetProperty("description").GetString()!;
        var arguments = tool.GetProperty("inputSchema").GetProperty("properties");
        var size = $"size {SimpleNote.Width}x{SimpleNote.Height}";

        Assert.Contains(PrinterTools.TextModeRule, description);
        Assert.Contains("Example with text: " + PrinterTools.PrintTextExample, description);
        Assert.Contains($"a headline (Bold at {size}, {SimpleNote.HeaderColumns} characters per line, centered)", PrinterTools.TextModeRule);
        Assert.Contains($"FontB at {size}, {SimpleNote.BodyColumns} characters per line", PrinterTools.TextModeRule);
        Assert.Contains($"a line of {StripMarkup.MinRuleLength} or more '=' or of {StripMarkup.MinRuleLength} or more '-' a rule of {SimpleNote.SeparatorLength} characters", PrinterTools.TextModeRule);
        Assert.Contains($"text holds at most {StripMarkup.MaxLength} characters and {PrinterService.MaxBlocks} lines.", PrinterTools.TextModeRule);
        Assert.Contains("counts that line with the line breaks that the server adds", PrinterTools.TextModeRule);
        Assert.Contains("'Block N' in an error is line N + 1 of text", PrinterTools.TextModeRule);
        Assert.Contains($"At most {StripMarkup.MaxLength} characters.", arguments.GetProperty("text").GetProperty("description").GetString());
        Assert.Contains(BlockEnums.Names<Alignment>(), arguments.GetProperty("align").GetProperty("description").GetString());
        // Text mode offers no sound and no light.
        Assert.Contains("text never makes a sound or a light.", PrinterTools.TextModeRule);
        Assert.DoesNotContain("Signal", PrinterTools.TextModeRule);
        Assert.Contains("""{"text":"# TITLE\n===\nProse as it is.\n[ ] step"}""", PrinterTools.ServerInstructions);
    }
}
