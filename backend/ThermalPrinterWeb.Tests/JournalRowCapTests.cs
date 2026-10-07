using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Tests;

// The journal row of a job over the block limit holds no blocks: a small request must not make a large row.
// Through the real pipeline, for each entry path that takes a caller's block list or text.
public sealed class JournalRowCapTests
{
    // Short: 501 lines of it are inside the length limit of text mode.
    private const string Secret = "SECRETWORD";
    private const string PrintUrl = TestHttp.PrintUrl;
    private const string JobsUrl = PrintUrl + "/jobs";
    private const int Limit = PrinterService.MaxBlocks;

    private static readonly string OverLimitError = $"block count {Limit + 1} is over the limit of {Limit}";

    // One line of text mode is one block. A rule fits the paper 500 times; a body line holds text for the search.
    private static string Request(bool textMode, int blocks, string line)
        => textMode
            ? JsonSerializer.Serialize(new { text = string.Join('\n', Enumerable.Repeat(line, blocks)) })
            : JsonSerializer.Serialize(new { content = Enumerable.Repeat(new { type = "Text", content = line }, blocks) });

    // The answer as one text: the HTTP body or the text of the tool.
    private static async Task<(bool Printed, string Answer)> SendAsync(HttpClient client, bool overMcp, string json)
    {
        if (overMcp)
        {
            var (_, text) = await client.CallToolAsync("print", json);
            return (text == "Printed.", text);
        }

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        return (status == HttpStatusCode.OK, body);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var (status, body) = await client.SendJsonAsync(HttpMethod.Get, url);
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Print_AtTheBlockLimit_StoresTheBlocks(bool textMode, bool overMcp)
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();

        var (printed, answer) = await SendAsync(client, overMcp, Request(textMode, Limit, textMode ? "===" : "x"));
        var job = Assert.Single(await app.JournalRowsAsync());

        Assert.True(printed, answer);
        Assert.Equal(JobResult.Printed, job.Result);
        Assert.Equal(Limit, job.BlockCount);
        Assert.Equal(Limit, JsonDocument.Parse(job.Payload.Blocks!).RootElement.GetArrayLength());
        Assert.NotNull(job.Payload.Bytes);
        // A rule line has no text; a Text block has.
        Assert.Equal(textMode ? null : "x", job.Title);
        Assert.Equal(textMode ? null : string.Join('\n', Enumerable.Repeat("x", Limit)), job.Payload.PlainText);
        Assert.True((await GetJsonAsync(client, JobsUrl)).GetProperty("jobs")[0].GetProperty("canReprint").GetBoolean());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Print_OverTheBlockLimit_StoresNoBlocks(bool textMode, bool overMcp)
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        // The start of the host logs a Warning of its own.
        app.Logs.Entries.Clear();
        var json = Request(textMode, Limit + 1, Secret);

        var (printed, answer) = await SendAsync(client, overMcp, json);
        var job = Assert.Single(await app.JournalRowsAsync());

        Assert.False(printed);
        Assert.Contains(OverLimitError, answer);
        // The small facts stay. The count is in the error.
        Assert.Equal(JobResult.Validation, job.Result);
        Assert.Equal(OverLimitError, job.Error);
        Assert.Equal(overMcp ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest, job.HttpStatus);
        Assert.Equal(overMcp ? PrintJobLog.McpTransport("print") : PrintJobLog.HttpTransport, job.Transport);
        // The row of a job with no blocks: no second copy of what the caller sent.
        Assert.Null(job.BlockCount);
        Assert.Null(job.Title);
        Assert.Null(job.Payload.Blocks);
        Assert.Null(job.Payload.PlainText);
        Assert.Null(job.Payload.Bytes);
        await using (var db = app.JournalDb())
            Assert.Empty(db.PrintJobTexts);
        // The request is stored as it came: the one copy, under the request body limit.
        Assert.Contains(Secret, System.Text.Encoding.UTF8.GetString(job.Payload.Request!));
        if (!overMcp)
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(json), job.Payload.Request);
        // The print path logs the limit once, with numbers only. The journal logs nothing.
        var warning = Assert.Single(app.Logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal($"Rejected print: the document has {Limit + 1} blocks, the limit is {Limit}", warning.Message);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));

        // The read paths: a row with no blocks, like a job that was refused before the print path.
        // The search and the ledger: no fault on such a row (a rejected job is in no ledger, with or without a title).
        var listed = (await GetJsonAsync(client, JobsUrl)).GetProperty("jobs")[0];
        Assert.False(listed.GetProperty("canReprint").GetBoolean());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("blockCount").ValueKind);
        var detail = await GetJsonAsync(client, $"{JobsUrl}/{job.Id}");
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("blocks").ValueKind);
        Assert.Equal(0, (await GetJsonAsync(client, $"{JobsUrl}/search?q={Secret}")).GetProperty("hits").GetArrayLength());
        Assert.Equal(0, (await GetJsonAsync(client, $"{JobsUrl}/papercuts")).GetProperty("strips").GetInt32());

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, $"{JobsUrl}/{job.Id}/reprint");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(PrintJournalReader.NoBlocksReason, JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        var (_, tool) = await client.CallToolAsync("reprint_job", $$"""{"id":"{{job.Id}}"}""");
        Assert.Equal(PrinterTools.NotPrinted(PrintJournalReader.NoBlocksReason), tool);

        // The MCP reads answer, with no text of the job.
        foreach (var (name, arguments) in new[] { ("list_jobs", "{}"), ("get_job", $$"""{"id":"{{job.Id}}"}""") })
        {
            var (isError, read) = await client.CallToolAsync(name, arguments);
            Assert.False(isError, read);
            Assert.StartsWith(JournalTools.UntrustedNotice, read);
            Assert.DoesNotContain(Secret, read);
        }
    }

    // The request of the finding: 3 bytes per block in, about 250 bytes per block in Blocks.
    [Fact]
    public async Task PostPrinter_EmptyBlocksOverTheLimit_StoreNoBlocks()
    {
        await using var app = new NoPrinterApp();
        var json = $$"""{"content":[{{string.Join(',', Enumerable.Repeat("{}", Limit + 1))}}]}""";

        var (status, _) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        var job = Assert.Single(await app.JournalRowsAsync());

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(OverLimitError, job.Error);
        Assert.Null(job.Payload.Blocks);
        Assert.Equal(json.Length, job.RequestBytes);
        // What the row would hold for each block of 3 request bytes.
        var perBlock = PrintJobEntry.BlocksJson([.. Enumerable.Repeat(new PrintContent(), 1000)]).Length / 1000;
        Assert.InRange(perBlock, 200, 300);
    }

    // The image and signal count limits need no rule of their own. An Image block is a hash of about 85 characters, whatever the picture,
    // and a Signal block has no content: 500 of them have a largest size. A Text block has none: its content is stored as sent.
    // No pin of the block count rule: the sizes that the PR text states.
    [Fact]
    public async Task PostPrinter_OverTheImageOrSignalLimit_StoresBlocksOfABoundedSize()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        var image = new { type = "Image", content = TestImages.PngBase64() };
        var images = JsonSerializer.Serialize(new { content = Enumerable.Repeat(image, Limit) });
        var signals = JsonSerializer.Serialize(new { content = Enumerable.Repeat(new { type = "Signal" }, Limit) });

        var first = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, images);
        var second = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, signals);
        var rows = await app.JournalRowsAsync();

        Assert.Equal(HttpStatusCode.BadRequest, first.Status);
        Assert.Equal(HttpStatusCode.BadRequest, second.Status);
        Assert.Equal($"image block count {Limit} is over the limit of {PrinterService.MaxImageBlocks}", rows[0].Error);
        Assert.Equal($"signal block count {Limit} is over the limit of {PrinterService.MaxSignalBlocks}", rows[1].Error);
        Assert.DoesNotContain(TestImages.PngBase64(), rows[0].Payload.Blocks);
        Assert.InRange(rows[0].Payload.Blocks!.Length, 1, Limit * 400);
        Assert.InRange(rows[1].Payload.Blocks!.Length, 1, Limit * 400);
    }
}
