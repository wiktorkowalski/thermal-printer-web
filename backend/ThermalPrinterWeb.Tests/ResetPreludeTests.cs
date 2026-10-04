using System.Net;
using System.Text;
using System.Text.Json;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Printing;

namespace ThermalPrinterWeb.Tests;

// Every job starts with ESC @, then the code page, then the options (#45).
// ESC @ resets the code page of the printer: Polish letters are wrong until ESC t n comes again.
public sealed class ResetPreludeTests
{
    private const string Polish = "Zażółć gęślą jaźń";

    private static readonly byte[] Reset = [0x1B, 0x40];
    private static readonly byte[] SelectPc852 = [0x1B, 0x74, 18];
    private static readonly byte[] SelectPc437 = [0x1B, 0x74, 0];
    private static readonly byte[] ResetLineSpacing = [0x1B, 0x32];

    private static PrintContent Text(string content = "ok") => new() { Type = ContentType.Text, Content = content };

    private static int Count(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> command) => bytes.Count(command);

    private static bool IsCodePageCommand(byte[] command) => command is [0x1B, 0x74, _];

    [Fact]
    public async Task BuildDocumentAsync_NoOptions_StartsWithResetThenTheDefaultCodePage()
    {
        var commands = await TestBlocks.NewService().BuildDocumentAsync([Text(Polish)], null);

        Assert.Equal(Reset, commands[0]);
        Assert.Equal(SelectPc852, commands[1]);
        Assert.Single(commands, command => command.AsSpan().SequenceEqual(Reset));
        Assert.Single(commands, IsCodePageCommand);
        // The text is in the code page that the prelude selects.
        var polish = Encoding.GetEncoding(852).GetBytes(Polish);
        Assert.Contains(commands, command => command.AsSpan().StartsWith(polish));
    }

    [Fact]
    public async Task BuildDocumentAsync_CodePageAndLineSpacingOptions_ComeRightAfterTheReset()
    {
        var options = new PrintOptions { CodePage = "PC437", DefaultLineSpacing = 40 };

        var commands = await TestBlocks.NewService().BuildDocumentAsync([Text()], options);

        Assert.Equal(Reset, commands[0]);
        Assert.Equal(SelectPc437, commands[1]);
        // ESC 3 n
        Assert.Equal([0x1B, 0x33, 40], commands[2]);
        Assert.Single(commands, command => command.AsSpan().SequenceEqual(Reset));
    }

    // The printer has its own default page after the reset; no page command goes out.
    [Fact]
    public async Task BuildDocumentAsync_UnknownCodePage_StillStartsWithTheReset()
    {
        var commands = await TestBlocks.NewService().BuildDocumentAsync([Text()], new PrintOptions { CodePage = "nope" });

        Assert.Equal(Reset, commands[0]);
        Assert.DoesNotContain(commands, IsCodePageCommand);
    }

    [Fact]
    public async Task BuildDocumentAsync_CodePageBlockMidDocument_AddsNoSecondReset()
    {
        List<PrintContent> content = [Text(Polish), new() { Type = ContentType.CodePage, Content = "PC437" }, Text("after")];

        var commands = await TestBlocks.NewService().BuildDocumentAsync(content, null);

        Assert.Equal(Reset, commands[0]);
        Assert.Single(commands, command => command.AsSpan().SequenceEqual(Reset));
        Assert.Equal([SelectPc852, SelectPc437], commands.Where(IsCodePageCommand));
        // The block switches the page between the two texts.
        var switchAt = commands.FindIndex(command => command.AsSpan().SequenceEqual(SelectPc437));
        Assert.True(commands.FindIndex(command => command.AsSpan().StartsWith("after"u8)) > switchAt);
        Assert.True(commands.FindIndex(command => command.AsSpan().StartsWith(Encoding.GetEncoding(852).GetBytes(Polish))) < switchAt);
    }

    // The reset of the next job does what ESC 2 did at the end of this one.
    [Fact]
    public async Task BuildDocumentAsync_CustomLineSpacing_EndsWithTheCutAndNoSpacingReset()
    {
        var commands = await TestBlocks.NewService().BuildDocumentAsync([Text()], new PrintOptions { DefaultLineSpacing = 80 });

        // GS V 65 3
        Assert.Equal([0x1D, 0x56, 0x41, 3], commands[^1]);
        Assert.DoesNotContain(commands, command => command.AsSpan().SequenceEqual(ResetLineSpacing));
    }

    // Two jobs in a row, the first with a custom line spacing: the second starts from the reset state.
    [Fact]
    public async Task BuildDocumentAsync_JobAfterAJobWithLineSpacing_StartsWithTheReset()
    {
        var service = TestBlocks.NewService();
        await service.BuildDocumentAsync([Text()], new PrintOptions { DefaultLineSpacing = 80 });

        var commands = await service.BuildDocumentAsync([Text()], null);

        Assert.Equal(Reset, commands[0]);
        Assert.DoesNotContain(commands, command => command.AsSpan().StartsWith([(byte)0x1B, (byte)0x33]));
    }

    // The bytes on the connection: the library that sends the job adds no reset of its own,
    // and a reprint is built again from the stored blocks, with one prelude.
    [Fact]
    public async Task PrintThenReprint_OnTheWire_EachJobHasOnePreludeAndTheSameBytes()
    {
        await using var printer = new WirePrinter();
        await using var app = new LoopbackPrinterApp(printer.Port);
        var client = app.CreateClient();
        var json = JsonSerializer.Serialize(new
        {
            content = new object[] { new { type = "Text", content = Polish } },
            options = new { codePage = "PC852", defaultLineSpacing = 40 }
        });

        var (printStatus, printBody) = await client.SendJsonAsync(HttpMethod.Post, "/api/printer", json);
        var first = await printer.NextJobAsync();
        var job = Assert.Single(await app.JournalRowsAsync());
        var (reprintStatus, reprintBody) = await client.SendJsonAsync(HttpMethod.Post, $"/api/printer/jobs/{job.Id}/reprint");
        var second = await printer.NextJobAsync();

        Assert.True(printStatus == HttpStatusCode.OK, printBody);
        Assert.True(reprintStatus == HttpStatusCode.OK, reprintBody);
        Assert.Equal(first, second);
        Assert.Equal(first, job.Payload.Bytes);
        Assert.Equal([.. Reset, .. SelectPc852, 0x1B, 0x33, 40], first[..8]);
        Assert.Equal(1, Count(first, Reset));
        Assert.Equal(1, Count(second, Reset));
        Assert.Empty(printer.Jobs);
    }

    // The prelude is bytes only: no paper.
    [Fact]
    public async Task BuildDocumentAsync_EmptyDocumentWithoutCut_IsThePreludeOnly()
    {
        var bytes = await TestBlocks.JobBytesAsync([], new PrintOptions { AutoCut = false });

        Assert.Equal([.. Reset, .. SelectPc852], bytes);
    }

    [Fact]
    public void CodePages_Pc852_IsPage18OnThePrinter()
    {
        Assert.Equal(18, (int)CodePages.Resolve("PC852")!.Value);
    }
}
