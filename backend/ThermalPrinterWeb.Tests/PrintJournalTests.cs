using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Controllers;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Tests;

// The print journal through the real pipeline. Each test has its own host and its own journal directory.
public sealed class PrintJournalTests
{
    private const string Secret = TestBlocks.Secret;
    private const string PrintJson = TestHttp.PrintJson;
    private const string HttpSimple = "http-simple";
    private const string HttpTemplate = "http-template";
    private const string McpPrint = "mcp:print";
    private const string McpPrintNote = "mcp:print_note";
    private const string PrintUrl = "/api/printer";

    // 23 characters: simple mode wraps a title at 24, and the journal title is the first line.
    private const string JobTitle = "T " + Secret;

    private static readonly string JournalCategory = typeof(PrintJournal).FullName!;

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

        public List<(LogLevel Level, string Category, string Message)> JournalLogs()
            => [.. Logs.Entries.Where(entry => entry.Category == JournalCategory)];
    }

    private sealed class ThrowingStore : IPrintJournalStore
    {
        public Task<string> OpenAsync(CancellationToken cancellationToken) => Task.FromResult("throwing-store");

        public Task AddAsync(PrintJob job, CancellationToken cancellationToken) => throw new IOException("disk fault");

        public Task<int?> DeleteAsync(IReadOnlyList<Guid> jobIds, CancellationToken cancellationToken) => throw new IOException("disk fault");

        public Task CompactAsync(CancellationToken cancellationToken) => throw new IOException("disk fault");
    }

    // Ignores the token, like a file system that does not answer.
    private sealed class HangingStore : IPrintJournalStore
    {
        private readonly TaskCompletionSource _hang = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls;

        public Task<string> OpenAsync(CancellationToken cancellationToken) => Task.FromResult("hanging-store");

        public Task AddAsync(PrintJob job, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return _hang.Task;
        }

        public Task<int?> DeleteAsync(IReadOnlyList<Guid> jobIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CompactAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        // The end of a test: the host must not wait for the writer when it stops.
        public void Release() => _hang.TrySetResult();
    }

    // The settings name of the other homelab apps in place of Journal:DataPath.
    private sealed class DataPathVariableApp() : TestApp(Production)
    {
        protected override string JournalPathSetting => JournalOptions.DataPathVariable;

        protected override void ConfigurePrinter(IWebHostBuilder builder) => UseRecordingPrinter(builder, new RecordingPrinter());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var limit = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(limit.Elapsed < TimeSpan.FromSeconds(20), "The condition did not become true in time.");
            await Task.Delay(20);
        }
    }

    private static string Text(byte[]? bytes) => Encoding.UTF8.GetString(Assert.IsType<byte[]>(bytes));

    // Sends one print job with Secret as its content.
    private static Task SendAsync(HttpClient client, string path, string source, string userAgent)
    {
        var arguments = new Dictionary<string, object?> { ["source"] = source };
        if (path is HttpSimple or McpPrintNote)
        {
            arguments[path == HttpSimple ? "name" : "title"] = JobTitle;
            arguments["message"] = Secret;
        }
        else
        {
            arguments["content"] = new object[]
            {
                new { type = "Text", content = JobTitle, style = new[] { "Bold" } },
                new { type = "Text", content = Secret }
            };
            arguments["options"] = new { feedLinesAfterPrint = 5 };
        }

        var json = JsonSerializer.Serialize(arguments);
        return path is McpPrint or McpPrintNote
            ? client.CallToolAsync(path["mcp:".Length..], json, userAgent)
            : client.SendJsonAsync(HttpMethod.Post, PrintUrl, json, userAgent);
    }

    [Theory]
    [InlineData(HttpSimple, "http")]
    [InlineData(HttpTemplate, "http")]
    [InlineData(McpPrint, McpPrint)]
    [InlineData(McpPrintNote, McpPrintNote)]
    public async Task Print_EveryEntryPoint_StoresOneRowThatMatchesTheJob(string path, string transport)
    {
        await using var app = new NoPrinterApp();
        var before = DateTime.UtcNow;

        await SendAsync(app.CreateClient(), path, "claude-code", "test-agent/1.0");

        var job = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal(transport, job.Transport);
        Assert.Equal("claude-code", job.Source);
        Assert.Equal("test-agent/1.0", job.UserAgent);
        Assert.Equal(JobResult.Printed, job.Result);
        Assert.Null(job.Error);
        Assert.Equal(200, job.HttpStatus);
        Assert.Equal(JobTitle, job.Title);
        Assert.InRange(job.CreatedAt, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.InRange(job.DurationMs, 0, 60_000);
        Assert.False(string.IsNullOrWhiteSpace(job.AppVersion));
        Assert.True(job.BlockCount >= 2);
        Assert.True(job.PaperDots > 0);
        Assert.Contains("\"raw\":\"no printer\"", job.PrinterStatus);

        // The request as it came: the HTTP body, or the JSON-RPC call with the tool name.
        var request = Text(job.Payload.Request);
        Assert.Equal(job.RequestBytes, job.Payload.Request!.Length);
        Assert.Contains(Secret, request);
        Assert.Contains("\"source\":\"claude-code\"", request);
        if (transport != "http")
            Assert.Contains($"\"name\":\"{transport["mcp:".Length..]}\"", request);

        // The blocks that went to the print path, in the JSON of the API.
        var blocks = JsonSerializer.Deserialize<List<PrintContent>>(job.Payload.Blocks!, PrintJobEntry.ApiJson)!;
        Assert.Equal(job.BlockCount, blocks.Count);
        Assert.Equal(JobTitle, blocks.First(block => block.Type == ContentType.Text).Content);
        Assert.Contains("\"type\":\"Text\"", job.Payload.Blocks);
        Assert.StartsWith(JobTitle + "\n", job.Payload.PlainText);

        // The printer bytes are what the service builds from the stored blocks.
        var service = (PrinterService)app.Services.GetRequiredService<IPrinterService>();
        var options = job.Payload.Options is null ? null : JsonSerializer.Deserialize<PrintOptions>(job.Payload.Options, PrintJobEntry.ApiJson);
        var rebuilt = (await service.BuildDocumentAsync(blocks, options)).SelectMany(bytes => bytes).ToArray();
        Assert.Equal(rebuilt, job.Payload.Bytes);
        Assert.Equal(rebuilt.Length, job.ByteCount);
        Assert.Equal(path is HttpTemplate or McpPrint, job.Payload.Options?.Contains("\"feedLinesAfterPrint\":5") ?? false);

        Assert.Contains("\"User-Agent\":[\"test-agent/1.0\"]", job.Payload.Headers);
        Assert.Null(job.Payload.Exception);
        Assert.Contains($"Print job: transport={transport} source=\"claude-code\"", job.Payload.Log);

        // The journal holds the content; the log does not.
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
    }

    [Fact]
    public async Task Startup_LogsOneInformationLineWithTheDatabasePath()
    {
        await using var app = new App();

        await app.JournalRowsAsync();

        var line = Assert.Single(app.JournalLogs());
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal($"Journal: {Path.Combine(app.JournalDirectory, JournalOptions.DatabaseFileName)}", line.Message);
    }

    [Fact]
    public async Task Database_HasASchemaVersionWriteAheadLogAndOwnerOnlyFiles()
    {
        await using var app = new App();
        await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);
        await app.JournalRowsAsync();

        await using var db = app.JournalDb();
        var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.EndsWith("_InitialJournal", migrations[0]);
        Assert.Equal(db.Database.GetMigrations(), migrations);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await db.Database.OpenConnectionAsync();
        await using var pragma = db.Database.GetDbConnection().CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", await pragma.ExecuteScalarAsync());

        if (!OperatingSystem.IsWindows())
        {
            var file = Path.Combine(app.JournalDirectory, JournalOptions.DatabaseFileName);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(app.JournalDirectory));
        }
    }

    // The stored numbers: a change here breaks the rows that exist.
    [Fact]
    public void JobResult_Numbers_AreFixed()
    {
        Assert.Equal(0, (int)JobResult.Printed);
        Assert.Equal(1, (int)JobResult.Validation);
        Assert.Equal(2, (int)JobResult.Printer);
        Assert.Equal(3, (int)JobResult.Busy);
        Assert.Equal(4, (int)JobResult.Fault);
        Assert.Equal(5, Enum.GetValues<JobResult>().Length);
    }

    [Fact]
    public async Task Restart_KeepsTheRowsAndAddsToThem()
    {
        // Not the directory of a host: a host deletes its own when it stops.
        var directory = TestApp.NewJournalDirectory();
        try
        {
            await using (var first = new App(null, ("Journal:DataPath", directory)))
            {
                await first.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, """{"name":"first","message":"m"}""");
                Assert.Single(await first.JournalRowsAsync());
            }

            await using var second = new App(null, ("Journal:DataPath", directory));
            await second.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, """{"name":"second","message":"m"}""");

            var rows = await second.JournalRowsAsync();
            Assert.Equal(["first", "second"], rows.Select(row => row.Title));
            await using var db = second.JournalDb();
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    // A database from before a migration: the app adds the new column and keeps the rows.
    [Fact]
    public async Task Start_DatabaseWithTheFirstSchema_KeepsItsRows()
    {
        const string OldId = "01999999-0000-7000-8000-000000000001";
        var directory = TestApp.NewJournalDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var options = new DbContextOptionsBuilder<JournalDbContext>()
                .UseSqlite(JournalDbContext.ConnectionString(Path.Combine(directory, JournalOptions.DatabaseFileName)))
                .Options;
            await using (var old = new JournalDbContext(options))
            {
                await old.GetService<IMigrator>().MigrateAsync("InitialJournal");
                await old.Database.ExecuteSqlAsync(
                    $"INSERT INTO PrintJobs (Id, CreatedAt, DurationMs, Transport, Result, HttpStatus, Title, RequestBytes, AppVersion) VALUES ({OldId}, '2026-10-01 12:00:00', 1, 'http', 0, 200, 'old', 0, 'test')");
                await old.Database.ExecuteSqlAsync($"INSERT INTO PrintJobPayloads (JobId, Headers) VALUES ({OldId}, {"{}"})");
            }

            await using var app = new App(null, ("Journal:DataPath", directory));
            await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);

            var rows = await app.JournalRowsAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(Guid.Parse(OldId), rows[0].Id);
            Assert.Equal("old", rows[0].Title);
            Assert.Null(rows[0].ReprintOf);
            await using var db = app.JournalDb();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PostPrinter_NoContentAndNoMessage_StoresTheRequestAsARejectedJob()
    {
        await using var app = new App();
        const string Json = """{"source":"claude-code","note":"SECRET-CALLER-CONTENT"}""";

        var (status, _) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, Json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var job = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal(JobResult.Validation, job.Result);
        Assert.Equal(400, job.HttpStatus);
        Assert.Equal("claude-code", job.Source);
        Assert.Equal(PrinterController.NoJobError, job.Error);
        Assert.Equal(Json, Text(job.Payload.Request));
        Assert.Null(job.Payload.Blocks);
        Assert.Null(job.Payload.Bytes);
        Assert.Null(job.BlockCount);
    }

    [Fact]
    public async Task PostPrinter_BlockTheServiceRejects_StoresTheBlocksAndTheError()
    {
        await using var app = new NoPrinterApp();
        const string Json = """{"content":[{"type":"Text","content":"ok"},{"type":"Barcode","content":"zażółć"}]}""";

        var (status, _) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, Json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var job = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal(JobResult.Validation, job.Result);
        Assert.StartsWith("Block 1 (Barcode): ", job.Error);
        Assert.Equal(2, job.BlockCount);
        Assert.Contains("\"type\":\"Barcode\"", job.Payload.Blocks);
        Assert.Equal(Json, Text(job.Payload.Request));
        Assert.Null(job.Payload.Bytes);
        Assert.Null(job.PrinterStatus);
        Assert.Contains("Rejected print: block 1", job.Payload.Log);
    }

    // The job is whole: the printer did not take it. The bytes are stored, so it can be sent again.
    [Fact]
    public async Task PostPrinter_PrinterUnreachable_StoresTheBytesAndTheStatus()
    {
        await using var app = new ClosedPortApp();

        var (status, _) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        var job = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal(JobResult.Printer, job.Result);
        Assert.Equal(503, job.HttpStatus);
        Assert.Equal("Printer not ready: printer unreachable", job.Error);
        Assert.True(job.ByteCount > 0);
        Assert.Equal(job.ByteCount, job.Payload.Bytes!.Length);
        Assert.Contains("\"reachable\":false", job.PrinterStatus);
        Assert.Contains("\"notReadyReason\":\"printer unreachable\"", job.PrinterStatus);
        Assert.Contains("Failed to read printer status", job.Payload.Log);
    }

    [Fact]
    public async Task Print_ServerBusy_StoresABusyJob()
    {
        await using var app = new App();
        app.Printer.Result = PrintResult.Busy;
        var client = app.CreateClient();

        await client.SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);
        await client.CallToolAsync("print", PrintJson);

        var rows = await app.JournalRowsAsync();
        Assert.Equal([JobResult.Busy, JobResult.Busy], rows.Select(row => row.Result));
        Assert.Equal([503, 200], rows.Select(row => row.HttpStatus));
        Assert.Equal(["http", McpPrint], rows.Select(row => row.Transport));
    }

    // The controller does not run for these: the row holds the request and the HTTP status.
    [Theory]
    [InlineData("""{"content":[{"type":"Text","content":"SECRET-CALLER-CONTENT" """, "application/json", 400)]
    [InlineData("""{"content":[{"type":"NoSuchType"}]}""", "application/json", 400)]
    [InlineData(PrintJson, "text/plain", 415)]
    public async Task PostPrinter_RequestThatDoesNotBind_StoresTheRequest(string body, string contentType, int expectedStatus)
    {
        await using var app = new App();

        using var response = await app.CreateClient().PostAsync(PrintUrl, new StringContent(body, Encoding.UTF8, contentType));

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        var job = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal("http", job.Transport);
        Assert.Equal(JobResult.Validation, job.Result);
        Assert.Equal(expectedStatus, job.HttpStatus);
        Assert.Null(job.Source);
        Assert.Equal(body, Text(job.Payload.Request));
        Assert.Empty(app.Printer.Jobs);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
    }

    // A print call with the wrong shape is a refused job: missing arguments, or a value the SDK cannot bind.
    [Theory]
    [InlineData("print", """{"source":"claude-code"}""", "'content' or 'text' is missing")]
    [InlineData("print_note", """{"title":"SECRET-CALLER-CONTENT","source":"claude-code"}""", "'message' is missing")]
    [InlineData("print", """{"content":"SECRET-CALLER-CONTENT","source":"claude-code"}""", "'content' has the wrong JSON type")]
    public async Task McpPrint_WrongShape_StoresTheRequestAsARejectedJob(string tool, string arguments, string problem)
    {
        await using var app = new App();

        var (isError, _) = await app.CreateClient().CallToolAsync(tool, arguments);

        Assert.True(isError);
        var job = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal($"mcp:{tool}", job.Transport);
        Assert.Equal("claude-code", job.Source);
        Assert.Equal(JobResult.Validation, job.Result);
        Assert.Equal(problem, job.Error);
        Assert.Contains(arguments, Text(job.Payload.Request));
        Assert.Null(job.Payload.Blocks);
        Assert.Empty(app.Printer.Jobs);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
    }

    // Not print jobs: the buzzer (also with the wrong shape), the status, the tool list.
    [Fact]
    public async Task OtherRequests_StoreNoRow()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();

        await client.CallToolAsync("beep", """{"count":"many"}""");
        await client.CallToolAsync("beep", "{}");
        await client.CallToolAsync("get_status", "{}");
        await client.McpAsync("tools/list");
        await client.SendJsonAsync(HttpMethod.Post, "/api/printer/beep");
        await client.SendJsonAsync(HttpMethod.Get, "/api/printer/status");

        Assert.Empty(await app.JournalRowsAsync());
    }

    [Fact]
    public async Task Print_WithAnImage_StoresThePictureOnceInTheRequest()
    {
        await using var app = new NoPrinterApp();
        var image = TestImages.PngBase64(16, 16);
        var json = JsonSerializer.Serialize(new
        {
            content = new object[] { new { type = "Image", content = image }, new { type = "Text", content = "caption" } }
        });

        var (status, _) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, json);

        Assert.Equal(HttpStatusCode.OK, status);
        var job = Assert.Single(await app.JournalRowsAsync());
        Assert.Contains(image, Text(job.Payload.Request));
        Assert.DoesNotContain(image, job.Payload.Blocks);
        using var blocks = JsonDocument.Parse(job.Payload.Blocks!);
        Assert.Equal(PrintJobEntry.ImageHash(image), blocks.RootElement[0].GetProperty("content").GetString());
        Assert.Matches("^sha256:[0-9a-f]{64};chars=" + image.Length + "$", PrintJobEntry.ImageHash(image));
        Assert.Equal("caption", job.Title);
        Assert.True(job.ByteCount > 0);
    }

    [Fact]
    public async Task Print_CredentialHeaders_AreStoredWithoutTheirValues()
    {
        await using var app = new App();
        using var request = new HttpRequestMessage(HttpMethod.Post, PrintUrl) { Content = TestHttp.Json(PrintJson) };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer SECRET-TOKEN");
        request.Headers.TryAddWithoutValidation("Cookie", "session=SECRET-TOKEN");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "SECRET-TOKEN");
        request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "203.0.113.7");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7, 198.51.100.1");

        using var response = await app.CreateClient().SendAsync(request);

        var job = Assert.Single(await app.JournalRowsAsync());
        var headers = JsonSerializer.Deserialize<Dictionary<string, string[]>>(job.Payload.Headers)!;
        Assert.Equal([PrintJournalMiddleware.Redacted], headers["Authorization"]);
        Assert.Equal([PrintJournalMiddleware.Redacted], headers["Cookie"]);
        Assert.Equal([PrintJournalMiddleware.Redacted], headers["X-Api-Key"]);
        Assert.Equal(["203.0.113.7"], headers["CF-Connecting-IP"]);
        Assert.Equal(["203.0.113.7, 198.51.100.1"], headers["X-Forwarded-For"]);
        Assert.DoesNotContain("SECRET-TOKEN", job.Payload.Headers);
    }

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("Proxy-Authorization", true)]
    [InlineData("cookie", true)]
    [InlineData("X-Auth-Token", true)]
    [InlineData("X-Api-Key", true)]
    [InlineData("Cf-Access-Jwt-Assertion", true)]
    [InlineData("CF-Access-Client-Secret", true)]
    [InlineData("Mcp-Session-Id", true)]
    [InlineData("X-Hub-Signature-256", true)]
    [InlineData("Passwd", true)]
    [InlineData("X-Credential", true)]
    [InlineData("X-Upstream-Bearer", true)]
    [InlineData("DPoP", true)]
    [InlineData("X-OTP", true)]
    [InlineData("Referer", false)]
    [InlineData("Accept-Encoding", false)]
    [InlineData("User-Agent", false)]
    [InlineData("CF-Connecting-IP", false)]
    [InlineData("X-Forwarded-For", false)]
    [InlineData("Content-Type", false)]
    [InlineData("Keep-Alive", false)]
    public void IsCredential_ByHeaderName(string name, bool expected)
        => Assert.Equal(expected, PrintJournalMiddleware.IsCredential(name));

    [Fact]
    public async Task Print_StoreThrows_ThePrintAnswersAndOneWarningIsLogged()
    {
        await using var app = new App(new ThrowingStore());
        var client = app.CreateClient();

        var (status, body) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, $$"""{"name":"{{Secret}}","message":"{{Secret}}"}""");
        var (isError, text) = await client.CallToolAsync("print", PrintJson);
        await app.JournalIdleAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"success":true,"error":null,"type":null}""", body);
        Assert.False(isError);
        Assert.Equal("Printed.", text);
        Assert.Equal(2, app.Printer.Jobs.Count);

        var warnings = app.JournalLogs().Where(entry => entry.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.Matches("^Journal write failed: job [0-9a-f-]{36} is not stored", warning.Message));
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
    }

    [Fact]
    public async Task Print_StoreHangs_ThePrintAnswersAndTheWriteIsGivenUp()
    {
        var store = new HangingStore();
        await using var app = new App(store, ("Journal:WriteTimeout", "00:00:00.2"));
        var client = app.CreateClient();

        var answered = Stopwatch.StartNew();
        var (first, _) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);
        var (second, _) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);
        answered.Stop();
        await app.JournalIdleAsync();

        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal(HttpStatusCode.OK, second);
        // The second job did not wait for the write of the first one.
        Assert.Equal(2, store.Calls);
        var warnings = app.JournalLogs().Where(entry => entry.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.Contains(nameof(TimeoutException), warning.Message));
        Assert.InRange(answered.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        store.Release();
    }

    [Fact]
    public async Task Print_WriterBehind_DropsTheNewJobsAndEveryPrintAnswers()
    {
        var store = new HangingStore();
        await using var app = new App(store, ("Journal:WriteTimeout", "00:01:00"));
        var client = app.CreateClient();
        const int Jobs = PrintJournal.MaxPendingJobs + 3;

        for (var i = 0; i < Jobs; i++)
        {
            var (status, _) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);
            Assert.Equal(HttpStatusCode.OK, status);
        }

        // The writer holds one entry in the store; the others wait or are dropped.
        await WaitForAsync(() => store.Calls == 1);
        Assert.Equal(Jobs, app.Printer.Jobs.Count);
        var dropped = app.JournalLogs().Where(entry => entry.Message.StartsWith("Journal is behind: job ", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, dropped.Count);
        Assert.All(dropped, entry => Assert.Equal(LogLevel.Warning, entry.Level));

        // The writer catches up: the jobs that waited are stored.
        store.Release();
        await app.JournalIdleAsync();
        Assert.Equal(PrintJournal.MaxPendingJobs, store.Calls);
    }

    [Theory]
    [InlineData("Journal:MaxDatabaseBytes", "1000", "Journal:MaxDatabaseBytes is 1000")]
    [InlineData("Journal:MinFreeBytes", "9000000000000000000", "Journal:MinFreeBytes is 9000000000000000000")]
    public async Task Print_PastASizeLimit_StoresNothingMoreAndLogsOneWarning(string setting, string value, string reason)
    {
        await using var app = new App(null, (setting, value));
        var client = app.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var (status, _) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);
            Assert.Equal(HttpStatusCode.OK, status);
        }

        Assert.Empty(await app.JournalRowsAsync());
        var warning = Assert.Single(app.JournalLogs(), entry => entry.Level == LogLevel.Warning);
        Assert.StartsWith("Journal is full: ", warning.Message);
        Assert.Contains(reason, warning.Message);
        Assert.EndsWith("Prints are not stored until there is space.", warning.Message);
    }

    [Fact]
    public async Task Print_ManyJobsAtOnce_StoresEachOne()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        // Under the queue limit: a burst above it can lose rows by design.
        const int Jobs = PrintJournal.MaxPendingJobs - 4;

        var sends = Enumerable.Range(0, Jobs).Select<int, Task>(i => i % 2 == 0
            ? client.SendJsonAsync(HttpMethod.Post, PrintUrl, $$"""{"name":"job-{{i}}","message":"m","source":"s{{i}}"}""")
            : client.CallToolAsync("print_note", $$"""{"title":"job-{{i}}","message":"m","source":"s{{i}}"}"""));
        await Task.WhenAll(sends);

        var rows = await app.JournalRowsAsync();
        Assert.Equal(Jobs, rows.Count);
        Assert.Equal(Jobs, rows.Select(row => row.Id).Distinct().Count());
        // Each row holds the facts of its own request.
        Assert.All(rows, row => Assert.Equal("s" + row.Title!["job-".Length..], row.Source));
        Assert.All(rows, row => Assert.Contains($"\"{row.Title}\"", Text(row.Payload.Request)));
        Assert.All(rows, row => Assert.Single(row.Payload.Log!.Split('\n'), line => line.Contains("Print job:")));
    }

    [Fact]
    public async Task Startup_PathThatCannotBeCreated_TheAppStartsAndPrints()
    {
        // A directory under a file: no user can create it, root included.
        var file = Path.GetTempFileName();
        try
        {
            await using var app = new App(null, ("Journal:DataPath", Path.Combine(file, "journal")));
            var client = app.CreateClient();
            await app.JournalIdleAsync();

            var (status, _) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);
            var (_, text) = await client.CallToolAsync("print", PrintJson);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("Printed.", text);
            var line = Assert.Single(app.JournalLogs());
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.StartsWith("Journal is off: it did not start. Prints are not stored.", line.Message);
            Assert.DoesNotContain(app.Logs.Entries, entry => entry.Level >= LogLevel.Error);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("Journal:DataPath", "", LogLevel.Information, "Journal: off (Journal:DataPath is empty)")]
    [InlineData("Journal:MaxDatabaseBytes", "0", LogLevel.Warning, "Journal is off: Journal:MaxDatabaseBytes 0 must be above 0. Prints are not stored.")]
    [InlineData("Journal:WriteTimeout", "3", LogLevel.Warning, "Journal is off: Journal:WriteTimeout 3.00:00:00 is outside the range")]
    [InlineData("Journal:MaxDatabaseBytes", "many", LogLevel.Warning, "Journal is off: it did not start. Prints are not stored.")]
    public async Task Startup_JournalOffOrBadSetting_TheAppStartsAndPrints(string setting, string value, LogLevel level, string message)
    {
        await using var app = new App(null, (setting, value));
        var client = app.CreateClient();
        await app.JournalIdleAsync();

        var (status, _) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);

        Assert.Equal(HttpStatusCode.OK, status);
        var line = Assert.Single(app.JournalLogs());
        Assert.Equal(level, line.Level);
        Assert.StartsWith(message, line.Message);
        Assert.False(Directory.Exists(app.JournalDirectory));
    }

    // The name the other homelab apps use. Journal:DataPath wins over it.
    [Fact]
    public async Task DataPathVariable_SetsTheDirectory()
    {
        await using var app = new DataPathVariableApp();
        await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);

        Assert.Single(await app.JournalRowsAsync());
        Assert.True(File.Exists(Path.Combine(app.JournalDirectory, JournalOptions.DatabaseFileName)));
    }

    // The production case until the owner mounts a volume: the journal works and says that it is lost on a redeploy.
    [Fact]
    public async Task Startup_DirectoryOnTheContainerLayer_StoresJobsAndLogsOneWarning()
    {
        await using var app = new App { InContainerLayer = true };

        var (status, _) = await app.CreateClient().SendJsonAsync(HttpMethod.Post, PrintUrl, PrintJson);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Single(await app.JournalRowsAsync());
        var logs = app.JournalLogs();
        Assert.Equal([LogLevel.Information, LogLevel.Warning], logs.Select(entry => entry.Level));
        Assert.Equal(
            $"Journal directory {app.JournalDirectory} is not a volume: the journal is lost when the container is replaced. Mount a volume there.",
            logs[1].Message);
    }

    [Fact]
    public void DefaultPath_IsTheDataDirectoryUnderTheContentRoot()
    {
        var options = new JournalOptions();

        Assert.Equal(Path.GetFullPath("/app/data/journal.db"), options.DatabasePath("/app"));
        Assert.Null(options.Error());
        Assert.False(options.IsOff);
    }

    [Theory]
    [InlineData("/app/data", false)]
    [InlineData("/data", true)]
    [InlineData("/data/journal", true)]
    [InlineData("/database", false)]
    public void IsOnMount_ReadsTheMountPoints(string directory, bool expected)
    {
        string[] mountInfo =
        [
            "1554 1280 0:256 / / rw,relatime master:413 - overlay overlay rw,lowerdir=/var/lib/docker/overlay2/l/A",
            "1555 1554 0:259 / /proc rw,nosuid,nodev,noexec,relatime - proc proc rw",
            "1565 1554 254:1 /docker/volumes/journal/_data /data rw,relatime - ext4 /dev/vda1 rw,discard",
            "1566 1554 254:1 /docker/containers/a/hosts /etc/hosts rw,relatime - ext4 /dev/vda1 rw,discard"
        ];

        Assert.Equal(expected, PrintJournal.IsOnMount(mountInfo, directory));
    }

    [Theory]
    [InlineData(null, 400, 1)]
    [InlineData(null, 413, 1)]
    [InlineData(null, 415, 1)]
    [InlineData(null, 500, 4)]
    [InlineData(null, 200, 4)]
    [InlineData(PrintFailure.Validation, 400, 1)]
    [InlineData(PrintFailure.Printer, 503, 2)]
    [InlineData(PrintFailure.Busy, 503, 3)]
    public void ResultOf_MapsThePrintResultOrTheStatus(PrintFailure? failure, int httpStatus, int expectedNumber)
    {
        var expected = (JobResult)expectedNumber;
        var result = failure switch
        {
            null => null,
            PrintFailure.Validation => PrintResult.Invalid("x"),
            PrintFailure.Busy => PrintResult.Busy,
            _ => PrintResult.PrinterFault("x")
        };

        Assert.Equal(expected, PrintJobEntry.ResultOf(result, httpStatus));
        Assert.Equal(JobResult.Printed, PrintJobEntry.ResultOf(PrintResult.Ok, 200));
    }

    // The hash runs in chunks. It must equal the hash of the whole text as UTF-8, the form in the request body.
    [Theory]
    [InlineData("")]
    [InlineData("QUJD")]
    [InlineData("zażółć \U0001F600")]
    [InlineData(null)]
    public void ImageHash_IsTheSha256OfTheUtf8Text(string? text)
    {
        // Null: a text longer than one chunk, with a surrogate pair across each chunk border.
        text ??= string.Concat(Enumerable.Repeat("abc\U0001F600ż", 5000));
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        Assert.Equal($"sha256:{expected};chars={text.Length}", PrintJobEntry.ImageHash(text));
    }

    [Fact]
    public void Trace_LogLines_AreLimited()
    {
        var trace = new PrintJobTrace();

        for (var i = 0; i < PrintJobTrace.MaxLogLines + 10; i++)
            trace.AddLogLine(new string('x', PrintJobTrace.MaxLogLineLength + 10));

        var lines = trace.LogLines();
        Assert.Equal(PrintJobTrace.MaxLogLines, lines.Length);
        Assert.All(lines, line => Assert.Equal(PrintJobTrace.MaxLogLineLength, line.Length));
    }
}
