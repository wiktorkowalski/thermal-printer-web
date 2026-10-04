using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Controllers;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Tests;

// The journal read endpoints and the reprint, through the real pipeline. The endpoints are public:
// these tests pin what an answer holds.
public sealed class JournalReadTests
{
    private const string Secret = TestBlocks.Secret;
    private const string PrintUrl = "/api/printer";
    private const string JobsUrl = "/api/printer/jobs";
    private const string ReprintTransport = "http:reprint";
    private const string PrintJobLine = "Print job:";

    // The only names a job answer may hold. A new name here is a decision: the endpoints have no auth.
    private static readonly string[] SummaryNames =
        ["id", "createdAt", "transport", "source", "result", "error", "title", "blockCount", "paperDots", "reprintOf", "canReprint"];
    private static readonly string[] ListNames = ["jobs", "next"];
    private static readonly string[] DetailNames = ["job", "blocks", "options"];

    private static readonly string ControllerCategory = typeof(PrintJobsController).FullName!;

    // A fake printer and a journal with the settings and the store of the test.
    private sealed class App(IPrintJournalStore? store = null, params (string Key, string? Value)[] settings) : TestApp(Production)
    {
        public RecordingPrinter Printer { get; } = new();

        protected override void ConfigurePrinter(IWebHostBuilder builder)
        {
            UseRecordingPrinter(builder, Printer);
            UseSettings(builder, settings);
            if (store is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPrintJournalStore>();
                    services.AddSingleton(store);
                });
            }
        }
    }

    // Says the journal is open and makes no database.
    private sealed class NoDatabaseStore : IPrintJournalStore
    {
        public Task<string> OpenAsync(CancellationToken cancellationToken) => Task.FromResult(Path.Combine("no-database", "journal.db"));

        public Task AddAsync(PrintJob job, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static App JournalOffApp() => new(null, ("Journal:DataPath", ""));

    private static string TextJob(string text, string? source = null)
        => JsonSerializer.Serialize(new { content = new object[] { new { type = "Text", content = text } }, source });

    private static string ImageJob(string base64)
        => JsonSerializer.Serialize(new { content = new object[] { new { type = "Text", content = "With a picture" }, new { type = "Image", content = base64 } } });

    private static async Task<PrintJob> PrintAsync(TestApp app, HttpClient client, string json)
    {
        await client.SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        return (await app.JournalRowsAsync())[^1];
    }

    private static Task<(HttpStatusCode Status, string Body)> ReprintAsync(HttpClient client, Guid id, string? source = null)
        => client.SendJsonAsync(HttpMethod.Post, $"{JobsUrl}/{id}/reprint{(source is null ? "" : $"?source={Uri.EscapeDataString(source)}")}");

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var (status, body) = await client.SendJsonAsync(HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(body).RootElement;
    }

    private static void AssertResponse(string body, string type, string error)
    {
        var response = JsonDocument.Parse(body).RootElement;
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Equal(type, response.GetProperty("type").GetString());
        Assert.Equal(error, response.GetProperty("error").GetString());
    }

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name)];

    private static PrintJob Row(Guid id, DateTime createdAt, JobResult result = JobResult.Printed) => new()
    {
        Id = id,
        CreatedAt = createdAt,
        Transport = "http",
        Result = result,
        HttpStatus = 200,
        AppVersion = "test",
        Payload = new PrintJobPayload { JobId = id, Headers = "{}" }
    };

    // Rows with ids in time order, oldest first.
    private static async Task<List<Guid>> AddRowsAsync(TestApp app, int count, Func<int, JobResult>? result = null)
    {
        await app.JournalIdleAsync();
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(0, count)
            .Select(index => Row(Guid.CreateVersion7(start.AddSeconds(index)), start.AddSeconds(index).UtcDateTime, result?.Invoke(index) ?? JobResult.Printed))
            .ToList();

        await using var db = app.JournalDb();
        db.PrintJobs.AddRange(rows);
        await db.SaveChangesAsync();
        return [.. rows.Select(row => row.Id)];
    }

    [Fact]
    public async Task List_EmptyJournal_AnswersAnEmptyPage()
    {
        await using var app = new App();
        var client = app.CreateClient();
        await app.JournalIdleAsync();

        var list = await GetJsonAsync(client, JobsUrl);

        Assert.Equal(0, list.GetProperty("jobs").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task List_Pages_NewestFirstWithNoRowTwice()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var ids = await AddRowsAsync(app, 5);

        var seen = new List<Guid>();
        string? before = null;
        var pages = 0;
        do
        {
            var page = await GetJsonAsync(client, $"{JobsUrl}?limit=2{(before is null ? "" : $"&before={before}")}");
            var jobs = page.GetProperty("jobs").EnumerateArray().Select(job => job.GetProperty("id").GetGuid()).ToList();
            Assert.InRange(jobs.Count, 1, 2);
            seen.AddRange(jobs);
            before = page.GetProperty("next").GetString();
            pages++;
        }
        while (before is not null);

        Assert.Equal(3, pages);
        Assert.Equal(ids.AsEnumerable().Reverse(), seen);
    }

    [Theory]
    [InlineData("", PrintJournalReader.DefaultPageSize)]
    [InlineData("?limit=1000", PrintJournalReader.MaxPageSize)]
    [InlineData("?limit=0", 1)]
    [InlineData("?limit=-5", 1)]
    public async Task List_Limit_StaysInsideTheCap(string query, int expected)
    {
        await using var app = new App();
        var client = app.CreateClient();
        await AddRowsAsync(app, PrintJournalReader.MaxPageSize + 5);

        var page = await GetJsonAsync(client, JobsUrl + query);

        Assert.Equal(expected, page.GetProperty("jobs").GetArrayLength());
        Assert.Equal(JsonValueKind.String, page.GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task List_PrintedOnly_LeavesOutEveryOtherResult()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var ids = await AddRowsAsync(app, 6, index => (JobResult)(index % 5));

        var all = await GetJsonAsync(client, JobsUrl);
        var printed = await GetJsonAsync(client, $"{JobsUrl}?printed=true");

        Assert.Equal(6, all.GetProperty("jobs").GetArrayLength());
        Assert.Equal(
            ["Printed", "Fault", "Busy", "Printer", "Validation", "Printed"],
            all.GetProperty("jobs").EnumerateArray().Select(job => job.GetProperty("result").GetString()));
        Assert.Equal([ids[5], ids[0]], printed.GetProperty("jobs").EnumerateArray().Select(job => job.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task List_CreatedAt_IsUtcWithItsMark()
    {
        await using var app = new App();
        var client = app.CreateClient();
        await AddRowsAsync(app, 1);

        var list = await GetJsonAsync(client, JobsUrl);

        Assert.Equal("2026-10-01T12:00:00Z", list.GetProperty("jobs")[0].GetProperty("createdAt").GetString());
    }

    // A new property on a record is a new public fact: this test fails until the allow-list has it.
    [Fact]
    public void Dtos_HoldTheAllowListedNamesOnly()
    {
        static string[] PropertyNames(Type type)
            => [.. type.GetProperties().Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))];

        Assert.Equal(SummaryNames, PropertyNames(typeof(PrintJobSummary)));
        Assert.Equal(ListNames, PropertyNames(typeof(PrintJobList)));
        Assert.Equal(DetailNames, PropertyNames(typeof(PrintJobDetail)));
    }

    [Fact]
    public async Task Answers_HoldNoCallerAddressNoHeaderNoLogAndNoRequest()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        const string agent = "agent-" + Secret;
        const string header = "header-" + Secret;
        var picture = TestImages.PngBase64();

        using (var request = new HttpRequestMessage(HttpMethod.Post, PrintUrl) { Content = TestHttp.Json(ImageJob(picture)) })
        {
            request.Headers.TryAddWithoutValidation("User-Agent", agent);
            request.Headers.TryAddWithoutValidation("X-Caller", header);
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.77");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var job = Assert.Single(await app.JournalRowsAsync());
        // The row holds what the answers must not.
        Assert.Equal(agent, job.UserAgent);
        Assert.Contains(header, job.Payload.Headers);
        Assert.NotNull(job.Payload.Log);

        var (_, list) = await client.SendJsonAsync(HttpMethod.Get, JobsUrl);
        var (_, detail) = await client.SendJsonAsync(HttpMethod.Get, $"{JobsUrl}/{job.Id}");
        var (_, reprint) = await ReprintAsync(client, job.Id);

        foreach (var body in new[] { list, detail, reprint })
        {
            Assert.DoesNotContain(Secret, body);
            Assert.DoesNotContain("203.0.113.77", body);
            Assert.DoesNotContain(picture, body);
            Assert.DoesNotContain(job.AppVersion, body);
            foreach (var name in new[] { "remoteIp", "userAgent", "headers", "exception", "log", "request", "bytes", "plainText", "printerStatus" })
                Assert.DoesNotContain($"\"{name}\"", body, StringComparison.OrdinalIgnoreCase);
        }

        // The names on the wire, not only the names of the records.
        var listed = JsonDocument.Parse(list).RootElement;
        Assert.Equal(ListNames, Names(listed));
        Assert.Equal(SummaryNames, Names(listed.GetProperty("jobs")[0]));
        var one = JsonDocument.Parse(detail).RootElement;
        Assert.Equal(DetailNames, Names(one));
        Assert.Equal(SummaryNames, Names(one.GetProperty("job")));
    }

    [Fact]
    public async Task Get_ImageJob_ServesTheHashInPlaceOfThePicture()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        var picture = TestImages.PngBase64();
        var job = await PrintAsync(app, client, ImageJob(picture));

        var detail = await GetJsonAsync(client, $"{JobsUrl}/{job.Id}");

        var blocks = detail.GetProperty("blocks");
        Assert.Equal(2, blocks.GetArrayLength());
        Assert.Equal("With a picture", blocks[0].GetProperty("content").GetString());
        Assert.Equal(PrintJobEntry.ImageHash(picture), blocks[1].GetProperty("content").GetString());
        Assert.True(detail.GetProperty("job").GetProperty("canReprint").GetBoolean());
        Assert.Equal("With a picture", detail.GetProperty("job").GetProperty("title").GetString());
    }

    // Row text is caller text: it leaves as JSON data, with no change and under the JSON content type.
    [Fact]
    public async Task Answers_HostileRowText_StaysData()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        const string hostile = "<script>alert(1)</script>\"'&</textarea>\u2028end";
        var job = await PrintAsync(app, client, TextJob(hostile, source: "<img src=x onerror=1>"));

        using var response = await client.GetAsync(JobsUrl);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var listed = JsonDocument.Parse(body).RootElement.GetProperty("jobs")[0];
        Assert.Equal(hostile, listed.GetProperty("title").GetString());
        Assert.Equal("<img src=x onerror=1>", listed.GetProperty("source").GetString());

        var detail = await GetJsonAsync(client, $"{JobsUrl}/{job.Id}");
        Assert.Equal(hostile, detail.GetProperty("blocks")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Answers_TellRobotsAndCachesToKeepOut()
    {
        await using var app = new App();
        var client = app.CreateClient();
        await app.JournalIdleAsync();

        using var response = await client.GetAsync(JobsUrl);

        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("GET", JobsUrl)]
    [InlineData("GET", JobsUrl + "/01999999-0000-7000-8000-000000000000")]
    [InlineData("POST", JobsUrl + "/01999999-0000-7000-8000-000000000000/reprint")]
    public async Task JournalOff_EveryEndpoint_Answers503WithItsType(string method, string url)
    {
        await using var app = JournalOffApp();
        var client = app.CreateClient();
        await app.JournalIdleAsync();

        var (status, body) = await client.SendJsonAsync(new HttpMethod(method), url);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        AssertResponse(body, PrintResponse.JournalType, PrintJobsController.JournalOffError);
        Assert.Empty(app.Printer.Jobs);
    }

    [Theory]
    [InlineData("GET", JobsUrl)]
    [InlineData("GET", JobsUrl + "/01999999-0000-7000-8000-000000000000")]
    [InlineData("POST", JobsUrl + "/01999999-0000-7000-8000-000000000000/reprint")]
    public async Task DatabaseFault_EveryEndpoint_Answers503AndLogsOneWarningWithNoDetailForTheCaller(string method, string url)
    {
        await using var app = new App(new NoDatabaseStore());
        var client = app.CreateClient();
        await app.JournalIdleAsync();

        var (status, body) = await client.SendJsonAsync(new HttpMethod(method), url);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        AssertResponse(body, PrintResponse.JournalType, PrintJobsController.JournalUnavailableError);
        Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(app.JournalDirectory, body);
        var warning = Assert.Single(app.Logs.Entries, entry => entry.Category == ControllerCategory);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith("Journal read failed", warning.Message);
        Assert.Empty(app.Printer.Jobs);
    }

    [Theory]
    [InlineData("GET", JobsUrl + "/not-an-id", PrintJobsController.InvalidIdError)]
    [InlineData("GET", JobsUrl + "/1%20OR%201=1", PrintJobsController.InvalidIdError)]
    [InlineData("GET", JobsUrl + "/01999999000070008000000000000000", PrintJobsController.InvalidIdError)]
    [InlineData("POST", JobsUrl + "/not-an-id/reprint", PrintJobsController.InvalidIdError)]
    [InlineData("GET", JobsUrl + "?before=not-an-id", PrintJobsController.InvalidCursorError)]
    public async Task BadId_Answers400(string method, string url, string error)
    {
        await using var app = new App();
        var client = app.CreateClient();
        await app.JournalIdleAsync();

        var (status, body) = await client.SendJsonAsync(new HttpMethod(method), url);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        AssertResponse(body, PrintResponse.ValidationType, error);
        Assert.Empty(app.Printer.Jobs);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "/reprint")]
    public async Task UnknownId_Answers404AndStoresNoRow(string method, string suffix)
    {
        await using var app = new App();
        var client = app.CreateClient();
        await app.JournalIdleAsync();

        var (status, body) = await client.SendJsonAsync(new HttpMethod(method), $"{JobsUrl}/{Guid.CreateVersion7()}{suffix}");

        Assert.Equal(HttpStatusCode.NotFound, status);
        AssertResponse(body, PrintResponse.ValidationType, PrintJobsController.NotFoundError);
        Assert.Empty(app.Printer.Jobs);
        Assert.Empty(await app.JournalRowsAsync());
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.StartsWith(PrintJobLine, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reprint_TextJob_PrintsTheSameBytesAndStoresANewRowThatNamesTheFirstJob()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        var original = await PrintAsync(app, client, JsonSerializer.Serialize(new
        {
            content = new object[]
            {
                new { type = "Text", content = "Title " + Secret, style = new[] { "Bold" }, alignment = "Left" },
                new { type = "QRCode", content = "https://example.com" }
            },
            options = new { feedLinesAfterPrint = 5, codePage = "PC852" },
            source = "first"
        }));

        var (status, body) = await ReprintAsync(client, original.Id, source: "web/tray");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("success").GetBoolean());
        var rows = await app.JournalRowsAsync();
        Assert.Equal(2, rows.Count);
        var reprint = rows[1];
        Assert.NotEqual(original.Id, reprint.Id);
        Assert.Equal(original.Id, reprint.ReprintOf);
        Assert.Null(original.ReprintOf);
        Assert.Equal(ReprintTransport, reprint.Transport);
        Assert.Equal("web/tray", reprint.Source);
        Assert.Equal(JobResult.Printed, reprint.Result);
        Assert.Equal(original.Title, reprint.Title);
        Assert.Equal(original.Payload.Blocks, reprint.Payload.Blocks);
        Assert.Equal(original.Payload.Options, reprint.Payload.Options);
        Assert.Equal(original.Payload.Bytes, reprint.Payload.Bytes);
        Assert.NotNull(reprint.PrinterStatus);

        // One line per job, and no row content in it.
        var lines = app.Logs.Entries.Where(entry => entry.Message.StartsWith(PrintJobLine, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("transport=http:reprint source=\"web/tray\"", lines[1].Message);
        Assert.Contains("result=Printed", lines[1].Message);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
    }

    public static TheoryData<string, string> ImageJobs()
    {
        var picture = TestImages.PngBase64(16, 16);
        // "+" and "/" as some serializers write them: the stored request is not the plain base64 text.
        var escaped = picture.Replace("+", "\\u002B").Replace("/", "\\/");
        return new TheoryData<string, string>
        {
            { "http", ImageJob(picture) },
            { "http-escaped", $$"""{"content":[{"type":"Image","content":"{{escaped}}"}]}""" },
            { "http-simple", JsonSerializer.Serialize(new { name = "Title", message = "Message", imageBase64 = picture }) },
            { "mcp:print", JsonSerializer.Serialize(new { content = new object[] { new { type = "Image", content = picture } } }) },
            { "mcp:print_note", JsonSerializer.Serialize(new { title = "Title", message = "Message", imageBase64 = picture }) }
        };
    }

    [Theory]
    [MemberData(nameof(ImageJobs))]
    public async Task Reprint_ImageJob_GetsThePictureBackFromTheStoredRequest(string path, string json)
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        if (path.StartsWith("mcp:", StringComparison.Ordinal))
            await client.CallToolAsync(path["mcp:".Length..], json);
        else
            await client.SendJsonAsync(HttpMethod.Post, PrintUrl, json);
        var original = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal(JobResult.Printed, original.Result);

        var (status, _) = await ReprintAsync(client, original.Id);
        // A reprint row has no picture in its own request: the picture comes from the first job.
        var (again, _) = await ReprintAsync(client, (await app.JournalRowsAsync())[^1].Id);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.OK, again);
        var rows = await app.JournalRowsAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows.Skip(1), row =>
        {
            Assert.Equal(original.Id, row.ReprintOf);
            Assert.Equal(original.Payload.Bytes, row.Payload.Bytes);
            Assert.Equal(original.Payload.Blocks, row.Payload.Blocks);
            Assert.Equal(JobResult.Printed, row.Result);
        });
    }

    [Fact]
    public async Task Reprint_JobThatFailsTheLimits_Answers400LikeTheFirstJob()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        var original = await PrintAsync(app, client, """{"content":[{"type":"Barcode","content":"żółć"}]}""");
        Assert.Equal(JobResult.Validation, original.Result);

        var (status, body) = await ReprintAsync(client, original.Id);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        AssertResponse(body, PrintResponse.ValidationType, original.Error!);
        var reprint = (await app.JournalRowsAsync())[^1];
        Assert.Equal(original.Id, reprint.ReprintOf);
        Assert.Equal(JobResult.Validation, reprint.Result);
    }

    [Fact]
    public async Task Reprint_JobWithNoBlocks_Answers400WithTheReason()
    {
        await using var app = new App();
        var client = app.CreateClient();
        // Cut in the middle: the body does not bind, so the job has no blocks.
        var original = await PrintAsync(app, client, "{\"content\":");
        Assert.Null(original.Payload.Blocks);

        var (status, body) = await ReprintAsync(client, original.Id);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        AssertResponse(body, PrintResponse.ValidationType, PrintJournalReader.NoBlocksReason);
        Assert.Empty(app.Printer.Jobs);
        var list = await GetJsonAsync(client, JobsUrl);
        Assert.False(list.GetProperty("jobs")[1].GetProperty("canReprint").GetBoolean());
    }

    [Fact]
    public async Task Reprint_ImageJobWithNoStoredRequest_Answers400WithTheReason()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var original = await PrintAsync(app, client, ImageJob(TestImages.PngBase64()));
        await using (var db = app.JournalDb())
        {
            var payload = await db.PrintJobPayloads.FindAsync(original.Id);
            payload!.Request = null;
            await db.SaveChangesAsync();
        }

        app.Printer.Jobs.Clear();
        var (status, body) = await ReprintAsync(client, original.Id);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        AssertResponse(body, PrintResponse.ValidationType, PrintJournalReader.NoImageReason);
        Assert.Empty(app.Printer.Jobs);
    }

    [Fact]
    public async Task Reprint_PrinterNotReady_Answers503Printer()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var original = await PrintAsync(app, client, TestHttp.PrintJson);
        app.Printer.Result = PrintResult.PrinterFault("Printer not ready: cover open");

        var (status, body) = await ReprintAsync(client, original.Id);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        AssertResponse(body, PrintResponse.PrinterType, "Printer not ready: cover open");
        var reprint = (await app.JournalRowsAsync())[^1];
        Assert.Equal(JobResult.Printer, reprint.Result);
        Assert.Equal(original.Id, reprint.ReprintOf);
    }

    [Fact]
    public async Task Reprint_PrintPathBusy_Answers503BusyWithRetryAfter()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var original = await PrintAsync(app, client, TestHttp.PrintJson);
        app.Printer.Result = PrintResult.Busy;

        using var response = await client.PostAsync($"{JobsUrl}/{original.Id}/reprint", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
        AssertResponse(await response.Content.ReadAsStringAsync(), PrintResponse.BusyType, PrintResult.Busy.Error!);
    }

    [Fact]
    public async Task Reprint_WhileAnotherReprintRuns_Answers503BusyAndPrintsNothing()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var original = await PrintAsync(app, client, TestHttp.PrintJson);
        app.Printer.Jobs.Clear();
        var reader = app.Services.GetRequiredService<PrintJournalReader>();
        Assert.True(reader.TryBeginReprint());

        using var busy = await client.PostAsync($"{JobsUrl}/{original.Id}/reprint", null);
        reader.EndReprint();
        var (after, _) = await ReprintAsync(client, original.Id);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal("5", Assert.Single(busy.Headers.GetValues("Retry-After")));
        AssertResponse(await busy.Content.ReadAsStringAsync(), PrintResponse.BusyType, PrintJobsController.ReprintBusyError);
        // The gate is free again after a reprint that ran.
        Assert.Equal(HttpStatusCode.OK, after);
        Assert.Single(app.Printer.Jobs);
        Assert.Equal(2, (await app.JournalRowsAsync()).Count);
    }

    [Fact]
    public async Task Reprint_OneCall_SendsOneJobWithTheStoredBlocksAndOptions()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var original = await PrintAsync(app, client, """{"content":[{"type":"Text","content":"x","alignment":"Right"},{"type":"Cut","partialCut":true}],"options":{"autoCut":false}}""");
        app.Printer.Jobs.Clear();

        var (status, _) = await ReprintAsync(client, original.Id);

        Assert.Equal(HttpStatusCode.OK, status);
        var (content, options) = Assert.Single(app.Printer.Jobs);
        Assert.Equal(2, content.Count);
        Assert.Equal(Alignment.Right, content[0].Alignment);
        Assert.Equal("x", content[0].Content);
        Assert.Equal(ContentType.Cut, content[1].Type);
        Assert.True(content[1].PartialCut);
        Assert.False(options!.AutoCut);
    }

    [Fact]
    public async Task Reprint_CallWithABody_StoresNoBody()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var original = await PrintAsync(app, client, TestHttp.PrintJson);

        var (status, _) = await client.SendJsonAsync(HttpMethod.Post, $"{JobsUrl}/{original.Id}/reprint", JsonSerializer.Serialize(new { junk = Secret }));

        Assert.Equal(HttpStatusCode.OK, status);
        var reprint = (await app.JournalRowsAsync())[^1];
        Assert.Equal(original.Id, reprint.ReprintOf);
        Assert.Null(reprint.Payload.Request);
        Assert.Equal(0, reprint.RequestBytes);
    }

    [Fact]
    public void TryRestoreImages_PictureNotInTheRequest_IsFalseAndKeepsTheHash()
    {
        var hash = PrintJobEntry.ImageHash(TestImages.PngBase64());
        List<PrintContent> content = [TestBlocks.ImageBlock(hash)];

        Assert.False(PrintJournalReader.TryRestoreImages(content, Encoding.UTF8.GetBytes(ImageJob(TestImages.PngBase64(4, 4)))));
        Assert.False(PrintJournalReader.TryRestoreImages(content, Encoding.UTF8.GetBytes("""{"content":[""")));
        Assert.False(PrintJournalReader.TryRestoreImages([TestBlocks.ImageBlock("not a hash")], "{}"u8.ToArray()));
        Assert.Equal(hash, content[0].Content);
    }
}
