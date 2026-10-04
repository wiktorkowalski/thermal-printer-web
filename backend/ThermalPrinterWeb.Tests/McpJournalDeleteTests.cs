using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Tests;

// The delete tools over real JSON-RPC. A delete cannot be undone: these tests pin what a call takes, what it leaves,
// and that nothing but an MCP call with the key deletes a row.
public sealed class McpJournalDeleteTests
{
    private const string Secret = TestBlocks.Secret;
    private const string DeleteJob = "delete_job";
    private const string DeleteJobs = "delete_jobs";
    private const string PrintUrl = "/api/printer";
    private const string JobsUrl = "/api/printer/jobs";
    private const string UnknownId = "01999999-0000-7000-8000-000000000000";
    private const string NotAvailable = "Not deleted: The print journal is not available";

    private static readonly string DeleterCategory = typeof(PrintJobDeleter).FullName!;
    private static readonly string JournalCategory = typeof(PrintJournal).FullName!;

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

        public List<string> AuditLines()
            => [.. Logs.Entries.Where(entry => entry.Category == DeleterCategory).Select(entry => entry.Message)];

        public async Task<(int Jobs, int Payloads, int Texts)> RowCountsAsync()
        {
            await this.JournalIdleAsync();
            await using var db = this.JournalDb();
            return (await db.PrintJobs.CountAsync(), await db.PrintJobPayloads.CountAsync(), await db.PrintJobTexts.CountAsync());
        }
    }

    // Opens like the real store, and every delete fails.
    private sealed class FailingDeleteStore : IPrintJournalStore
    {
        public Task<string> OpenAsync(CancellationToken cancellationToken) => Task.FromResult(Path.Combine("failing-store", "journal.db"));

        public Task AddAsync(PrintJob job, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<JobSelection> DeleteAsync(JobDeleteFilter filter, int? confirmRows, int maxRows, CancellationToken cancellationToken)
            => throw new IOException("disk fault " + Secret);

        public Task CompactAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // Deletes, and cannot make the file smaller.
    private sealed class NoCompactStore : IPrintJournalStore
    {
        public Task<string> OpenAsync(CancellationToken cancellationToken) => Task.FromResult(Path.Combine("no-compact-store", "journal.db"));

        public Task AddAsync(PrintJob job, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<JobSelection> DeleteAsync(JobDeleteFilter filter, int? confirmRows, int maxRows, CancellationToken cancellationToken)
            => Task.FromResult(new JobSelection(1, 0, filter.Id, filter.Id, Deleted: true));

        public Task CompactAsync(CancellationToken cancellationToken) => throw new IOException("disk full " + Secret);
    }

    private static string Args(object arguments) => JsonSerializer.Serialize(arguments);

    private static async Task<PrintJob> PrintAsync(App app, HttpClient client, string text, string? source = null)
    {
        var (status, body) = await client.SendJsonAsync(
            HttpMethod.Post, PrintUrl, Args(new { content = new object[] { new { type = "Text", content = text } }, source }));
        Assert.True(status == HttpStatusCode.OK, body);
        return (await app.JournalRowsAsync())[^1];
    }

    private static async Task<PrintJob> ReprintAsync(App app, HttpClient client, Guid id, string? source = null)
    {
        var (isError, answer) = await client.CallToolAsync("reprint_job", Args(new { id, source }));
        Assert.False(isError, answer);
        Assert.Equal("Printed.", answer);
        return (await app.JournalRowsAsync())[^1];
    }

    // Rows that a test puts into the database by hand: any number, any time.
    private static async Task<List<Guid>> AddRowsAsync(App app, int count, string? source, DateTime createdAt)
    {
        await app.JournalIdleAsync();
        var ids = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).ToList();
        await using var db = app.JournalDb();
        db.PrintJobs.AddRange(ids.Select(id => new PrintJob
        {
            Id = id,
            CreatedAt = createdAt,
            Transport = "http",
            Source = source,
            HttpStatus = 200,
            BlockCount = 1,
            AppVersion = "test",
            Payload = new PrintJobPayload { JobId = id, Headers = "{}", Blocks = "[]" },
            Text = new PrintJobText { JobId = id, Text = "text" }
        }));
        await db.SaveChangesAsync();
        return ids;
    }

    // The fixed first line, then one line of JSON.
    private static JsonElement AnswerJson(string answer, string notice)
    {
        var lines = answer.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(notice, lines[0]);
        return JsonDocument.Parse(lines[1]).RootElement;
    }

    private static void AssertCounts(JsonElement json, int rows, int jobs, int reprints)
    {
        Assert.Equal(rows, json.GetProperty("rows").GetInt32());
        Assert.Equal(jobs, json.GetProperty("jobs").GetInt32());
        Assert.Equal(reprints, json.GetProperty("reprints").GetInt32());
        Assert.Equal(PrintJobDeleter.MaxRows, json.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task ToolsList_DeleteTools_StateTheLimitTheDryRunAndThatADeleteIsForGood()
    {
        await using var app = new App();
        var client = app.CreateClient();

        var tools = (await client.McpAsync("tools/list")).GetProperty("tools").EnumerateArray().ToList();
        var one = tools.Single(tool => tool.GetProperty("name").GetString() == DeleteJob);
        var many = tools.Single(tool => tool.GetProperty("name").GetString() == DeleteJobs);

        var manyDescription = many.GetProperty("description").GetString();
        Assert.Contains($"at most {PrintJobDeleter.MaxRows} rows", manyDescription);
        Assert.Contains("Without confirm the call is a dry run", manyDescription);
        Assert.Contains("At least one of source, from and to is needed", manyDescription);
        Assert.All([one, many], tool => Assert.Contains("only when the user asks for that delete", tool.GetProperty("description").GetString()));
        Assert.StartsWith("Needed.", one.GetProperty("inputSchema").GetProperty("properties").GetProperty("id").GetProperty("description").GetString());
        Assert.All(
            many.GetProperty("inputSchema").GetProperty("properties").EnumerateObject(),
            argument => Assert.StartsWith("Optional.", argument.Value.GetProperty("description").GetString()));
        Assert.Contains("a filter, not the name of the caller", many.GetProperty("inputSchema").GetProperty("properties").GetProperty("source").GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DeleteJob, PrinterTools.ServerInstructions);
        Assert.Contains(DeleteJobs, PrinterTools.ServerInstructions);
    }

    [Fact]
    public async Task DeleteJob_JobWithReprints_DeletesTheJobItsPayloadItsTextAndItsReprintRows()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var original = await PrintAsync(app, client, "Title " + Secret, source: "first");
        var reprint = await ReprintAsync(app, client, original.Id);
        // A reprint of the reprint names the first job.
        var second = await ReprintAsync(app, client, reprint.Id);
        var other = await PrintAsync(app, client, "Other job");
        Assert.Equal((4, 4, 2), await app.RowCountsAsync());
        app.Logs.Entries.Clear();

        var (isError, answer) = await client.CallToolAsync(DeleteJob, Args(new { id = original.Id }), userAgent: "agent/1.0");

        Assert.False(isError, answer);
        var json = AnswerJson(answer, JournalTools.DeletedNotice);
        AssertCounts(json, rows: 3, jobs: 1, reprints: 2);
        Assert.Equal(original.Id, json.GetProperty("firstId").GetGuid());
        Assert.Equal(second.Id, json.GetProperty("lastId").GetGuid());
        Assert.False(json.TryGetProperty("dryRun", out _));
        Assert.Equal(["rows", "jobs", "reprints", "firstId", "lastId", "limit"], json.EnumerateObject().Select(property => property.Name));

        // The payload and the text of the job went with it: the cascade of the database.
        Assert.Equal((1, 1, 1), await app.RowCountsAsync());
        Assert.Equal(other.Id, Assert.Single(await app.JournalRowsAsync()).Id);
        var (_, gone) = await client.CallToolAsync("get_job", Args(new { id = original.Id }));
        Assert.Equal("Not read: Job not found", gone);
        var (_, reprintGone) = await client.CallToolAsync("reprint_job", Args(new { id = reprint.Id }));
        Assert.Equal("Not printed: Job not found", reprintGone);

        // One line says who deleted what. No row content, and the delete is no journal row.
        var line = Assert.Single(app.AuditLines());
        Assert.Equal(
            $"Journal delete: transport=mcp:delete_job userAgent=\"agent/1.0\" id={original.Id} source=\"-\" from=- to=- "
            + $"jobs=1 reprints=2 firstId={original.Id} lastId={second.Id}",
            line);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task DeleteJob_ReprintRow_DeletesOnlyThatRow()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var original = await PrintAsync(app, client, "Title");
        var reprint = await ReprintAsync(app, client, original.Id);

        var (_, answer) = await client.CallToolAsync(DeleteJob, Args(new { id = reprint.Id }));

        AssertCounts(AnswerJson(answer, JournalTools.DeletedNotice), rows: 1, jobs: 1, reprints: 0);
        Assert.Equal(original.Id, Assert.Single(await app.JournalRowsAsync()).Id);
        Assert.Equal((1, 1, 1), await app.RowCountsAsync());
    }

    [Fact]
    public async Task DeleteJob_UnknownId_SaysNotFoundAndDeletesNothing()
    {
        await using var app = new App();
        var client = app.CreateClient();
        await PrintAsync(app, client, "Title");

        var (isError, answer) = await client.CallToolAsync(DeleteJob, Args(new { id = UnknownId }));

        Assert.False(isError);
        Assert.Equal("Not deleted: Job not found", answer);
        Assert.Equal((1, 1, 1), await app.RowCountsAsync());
        Assert.Empty(app.AuditLines());
    }

    [Fact]
    public async Task DeleteJobs_DescriptionExample_IsADryRun()
    {
        await using var app = new App();
        var client = app.CreateClient();
        await PrintAsync(app, client, "Title", source: "uber-prints");

        var (isError, answer) = await client.CallToolAsync(DeleteJobs, JournalTools.DeleteJobsExample);

        Assert.False(isError, answer);
        Assert.True(AnswerJson(answer, JournalTools.DryRunNotice).GetProperty("dryRun").GetBoolean());
        Assert.Equal((1, 1, 1), await app.RowCountsAsync());
    }

    [Fact]
    public async Task DeleteJobs_WithoutConfirm_IsADryRunThatCountsAndDeletesNothing()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var first = await PrintAsync(app, client, "One", source: "burst");
        await PrintAsync(app, client, "Two", source: "burst");
        await PrintAsync(app, client, "Keep", source: "web/note");
        // A reprint of a burst job from another source: it goes with its first job.
        var reprint = await ReprintAsync(app, client, first.Id, source: "web/tray");
        app.Logs.Entries.Clear();

        var (isError, answer) = await client.CallToolAsync(DeleteJobs, Args(new { source = "burst" }));

        Assert.False(isError, answer);
        var json = AnswerJson(answer, JournalTools.DryRunNotice);
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        AssertCounts(json, rows: 3, jobs: 2, reprints: 1);
        Assert.Equal(first.Id, json.GetProperty("firstId").GetGuid());
        Assert.Equal(reprint.Id, json.GetProperty("lastId").GetGuid());
        Assert.False(json.TryGetProperty("overLimit", out _));
        // The filter text is not in the answer.
        Assert.DoesNotContain("burst", answer);
        Assert.Equal((4, 4, 3), await app.RowCountsAsync());
        Assert.Empty(app.AuditLines());
    }

    [Fact]
    public async Task DeleteJobs_ConfirmEqualToTheRows_DeletesExactlyThoseRows()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var first = await PrintAsync(app, client, "One " + Secret, source: "burst");
        await PrintAsync(app, client, "Two", source: "burst");
        var keep = await PrintAsync(app, client, "Keep", source: "web/note");
        // Not equal to the filter: the match is exact.
        var near = await PrintAsync(app, client, "Near", source: "burst2");
        var upper = await PrintAsync(app, client, "Upper", source: "BURST");
        var reprint = await ReprintAsync(app, client, first.Id, source: "web/tray");
        var (_, dryRun) = await client.CallToolAsync(DeleteJobs, Args(new { source = "burst" }));
        var rows = AnswerJson(dryRun, JournalTools.DryRunNotice).GetProperty("rows").GetInt32();
        app.Logs.Entries.Clear();

        var (isError, answer) = await client.CallToolAsync(DeleteJobs, Args(new { source = "burst", confirm = rows }));

        Assert.False(isError, answer);
        var json = AnswerJson(answer, JournalTools.DeletedNotice);
        AssertCounts(json, rows: 3, jobs: 2, reprints: 1);
        Assert.Equal(first.Id, json.GetProperty("firstId").GetGuid());
        Assert.Equal(reprint.Id, json.GetProperty("lastId").GetGuid());
        Assert.Equal([keep.Id, near.Id, upper.Id], (await app.JournalRowsAsync()).Select(job => job.Id));
        Assert.Equal((3, 3, 3), await app.RowCountsAsync());

        var line = Assert.Single(app.AuditLines());
        Assert.StartsWith("Journal delete: transport=mcp:delete_jobs userAgent=\"-\" id=- source=\"burst\" from=- to=- jobs=2 reprints=1 firstId=", line);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
    }

    [Theory]
    // Fewer, more, and the number of jobs without their reprint.
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public async Task DeleteJobs_ConfirmNotEqualToTheRows_DeletesNothing(int confirm)
    {
        await using var app = new App();
        var client = app.CreateClient();
        var first = await PrintAsync(app, client, "One", source: "burst");
        await PrintAsync(app, client, "Two", source: "burst");
        await ReprintAsync(app, client, first.Id);

        var (isError, answer) = await client.CallToolAsync(DeleteJobs, Args(new { source = "burst", confirm }));

        Assert.False(isError);
        var json = AnswerJson(answer, $"Not deleted: 3 rows fit now, and confirm is {confirm}. Run the call without confirm again.");
        AssertCounts(json, rows: 3, jobs: 2, reprints: 1);
        Assert.Equal((3, 3, 2), await app.RowCountsAsync());
        Assert.Empty(app.AuditLines());
    }

    // The set changed after the dry run: the count of the dry run no longer fits, and nothing is deleted.
    [Fact]
    public async Task DeleteJobs_AJobArrivesAfterTheDryRun_TheConfirmNoLongerFits()
    {
        await using var app = new App();
        var client = app.CreateClient();
        await PrintAsync(app, client, "One", source: "burst");
        var (_, dryRun) = await client.CallToolAsync(DeleteJobs, Args(new { source = "burst" }));
        var rows = AnswerJson(dryRun, JournalTools.DryRunNotice).GetProperty("rows").GetInt32();
        await PrintAsync(app, client, "Two", source: "burst");

        var (_, answer) = await client.CallToolAsync(DeleteJobs, Args(new { source = "burst", confirm = rows }));

        Assert.StartsWith("Not deleted: 2 rows fit now, and confirm is 1.", answer);
        Assert.Equal((2, 2, 2), await app.RowCountsAsync());
    }

    [Fact]
    public async Task DeleteJobs_NoJobFits_SaysSo()
    {
        await using var app = new App();
        var client = app.CreateClient();
        await PrintAsync(app, client, "One", source: "burst");

        var (_, dryRun) = await client.CallToolAsync(DeleteJobs, Args(new { source = "nobody" }));
        var (_, answer) = await client.CallToolAsync(DeleteJobs, Args(new { source = "nobody", confirm = 1 }));

        AssertCounts(AnswerJson(dryRun, JournalTools.DryRunNotice), rows: 0, jobs: 0, reprints: 0);
        AssertCounts(AnswerJson(answer, "Not deleted: No job fits the filters"), rows: 0, jobs: 0, reprints: 0);
        Assert.Equal((1, 1, 1), await app.RowCountsAsync());
    }

    [Theory]
    // "from" is in the range, "to" is not.
    [InlineData("2026-10-03T18:00:00Z", "2026-10-03T19:00:00Z", 2)]
    [InlineData("2026-10-03T18:00:00.0000000Z", "2026-10-03T19:00:00.0000001Z", 3)]
    [InlineData("2026-10-03T18:00:01Z", null, 2)]
    [InlineData(null, "2026-10-03T18:00:00Z", 1)]
    // A date is its first moment.
    [InlineData("2026-10-03", "2026-10-04", 3)]
    [InlineData("2026-10-04", null, 0)]
    // An offset: 20:00 at +02:00 is 18:00 UTC.
    [InlineData("2026-10-03T20:00:00+02:00", "2026-10-03T20:30:01+02:00", 2)]
    public async Task DeleteJobs_TimeRange_TakesTheJobsFromFromUpToTo(string? from, string? to, int expected)
    {
        await using var app = new App();
        var client = app.CreateClient();
        await AddRowsAsync(app, 1, "a", new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc));
        await AddRowsAsync(app, 1, "a", new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc));
        await AddRowsAsync(app, 1, "b", new DateTime(2026, 10, 3, 18, 30, 0, DateTimeKind.Utc));
        await AddRowsAsync(app, 1, "a", new DateTime(2026, 10, 3, 19, 0, 0, DateTimeKind.Utc));

        var (_, dryRun) = await client.CallToolAsync(DeleteJobs, Args(new { from, to }));
        AssertCounts(AnswerJson(dryRun, JournalTools.DryRunNotice), rows: expected, jobs: expected, reprints: 0);
        if (expected == 0)
            return;

        var (_, answer) = await client.CallToolAsync(DeleteJobs, Args(new { from, to, confirm = expected }));

        AssertCounts(AnswerJson(answer, JournalTools.DeletedNotice), rows: expected, jobs: expected, reprints: 0);
        Assert.Equal(4 - expected, (await app.RowCountsAsync()).Jobs);
    }

    [Fact]
    public async Task DeleteJobs_SourceAndTimeRange_TakesOnlyTheJobsThatFitBoth()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var at = new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
        await AddRowsAsync(app, 2, "a", at);
        var other = await AddRowsAsync(app, 1, "b", at);
        var later = await AddRowsAsync(app, 1, "a", at.AddDays(1));

        var (_, answer) = await client.CallToolAsync(DeleteJobs, Args(new { source = "a", from = "2026-10-03", to = "2026-10-04", confirm = 2 }));

        AssertCounts(AnswerJson(answer, JournalTools.DeletedNotice), rows: 2, jobs: 2, reprints: 0);
        Assert.Equal([other[0], later[0]], (await app.JournalRowsAsync()).Select(job => job.Id).Order());
        Assert.Contains("source=\"a\" from=2026-10-03T00:00:00.0000000Z to=2026-10-04T00:00:00.0000000Z jobs=2 reprints=0", Assert.Single(app.AuditLines()));
    }

    // One call deletes at most MaxRows rows. A call that fits more deletes nothing: it never deletes a part.
    [Fact]
    public async Task DeleteJobs_MoreRowsThanTheLimit_DeletesNothingUntilTheRangeIsShorter()
    {
        await using var app = new App();
        var client = app.CreateClient();
        const int Rows = PrintJobDeleter.MaxRows + 1;
        var ids = await AddRowsAsync(app, Rows, "many", new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc));

        var (_, dryRun) = await client.CallToolAsync(DeleteJobs, Args(new { source = "many" }));
        var (_, refused) = await client.CallToolAsync(DeleteJobs, Args(new { source = "many", confirm = Rows }));

        var preview = AnswerJson(dryRun, JournalTools.DryRunNotice);
        AssertCounts(preview, rows: Rows, jobs: Rows, reprints: 0);
        Assert.True(preview.GetProperty("overLimit").GetBoolean());
        var json = AnswerJson(refused, $"Not deleted: {Rows} rows fit, and one call deletes at most {PrintJobDeleter.MaxRows}. Use a shorter time range.");
        Assert.True(json.GetProperty("overLimit").GetBoolean());
        Assert.Equal(Rows, (await app.RowCountsAsync()).Jobs);
        Assert.Empty(app.AuditLines());

        // One row less: the call is at the limit and deletes every row.
        var (_, one) = await client.CallToolAsync(DeleteJob, Args(new { id = ids[0] }));
        AssertCounts(AnswerJson(one, JournalTools.DeletedNotice), rows: 1, jobs: 1, reprints: 0);
        var (_, deleted) = await client.CallToolAsync(DeleteJobs, Args(new { source = "many", confirm = PrintJobDeleter.MaxRows }));
        AssertCounts(AnswerJson(deleted, JournalTools.DeletedNotice), rows: PrintJobDeleter.MaxRows, jobs: PrintJobDeleter.MaxRows, reprints: 0);
        Assert.Equal((0, 0, 0), await app.RowCountsAsync());
    }

    // The filter value is a parameter, never SQL, and a wildcard is a plain character.
    [Theory]
    [InlineData("x' OR '1'='1")]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("burs_")]
    [InlineData("burst'; DELETE FROM PrintJobs; --")]
    public async Task DeleteJobs_SourceWithSqlInIt_FitsNoJob(string source)
    {
        await using var app = new App();
        var client = app.CreateClient();
        await PrintAsync(app, client, "One", source: "burst");

        var (_, dryRun) = await client.CallToolAsync(DeleteJobs, Args(new { source }));
        var (_, answer) = await client.CallToolAsync(DeleteJobs, Args(new { source, confirm = 1 }));

        AssertCounts(AnswerJson(dryRun, JournalTools.DryRunNotice), rows: 0, jobs: 0, reprints: 0);
        Assert.StartsWith("Not deleted: No job fits the filters", answer);
        Assert.Equal((1, 1, 1), await app.RowCountsAsync());
    }

    // The source of a job is caller text: a job whose source holds a line end can be deleted, and the log line stays one line.
    [Fact]
    public async Task DeleteJobs_SourceWithControlCharacters_IsCleanedInTheLogLine()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var source = "bad\nJournal delete: forged \"line\"";
        await AddRowsAsync(app, 1, source, DateTime.UtcNow);

        var (_, answer) = await client.CallToolAsync(DeleteJobs, Args(new { source, confirm = 1 }));

        AssertCounts(AnswerJson(answer, JournalTools.DeletedNotice), rows: 1, jobs: 1, reprints: 0);
        var line = Assert.Single(app.AuditLines());
        Assert.DoesNotContain('\n', line);
        Assert.Contains("source=\"bad?Journal delete: forged 'line'\"", line);
    }

    [Theory]
    [InlineData(DeleteJob, "{}", "'id' is missing")]
    [InlineData(DeleteJob, null, "'id' is missing")]
    [InlineData(DeleteJob, """{"jobId":"SECRET-CALLER-CONTENT"}""", "'id' is missing")]
    [InlineData(DeleteJob, """{"id":"SECRET-CALLER-CONTENT"}""", "'id' is not a job id")]
    [InlineData(DeleteJob, """{"id":"1 OR 1=1"}""", "'id' is not a job id")]
    [InlineData(DeleteJob, """{"id":"*"}""", "'id' is not a job id")]
    [InlineData(DeleteJob, """{"id":5}""", "'id' has the wrong JSON type")]
    [InlineData(DeleteJob, """{"id":["SECRET-CALLER-CONTENT"]}""", "'id' has the wrong JSON type")]
    // No filter: a call must not delete the whole journal by leaving the arguments out.
    [InlineData(DeleteJobs, "{}", "one of 'source', 'from' and 'to' is needed")]
    [InlineData(DeleteJobs, null, "one of 'source', 'from' and 'to' is needed")]
    [InlineData(DeleteJobs, """{"confirm":1}""", "one of 'source', 'from' and 'to' is needed")]
    [InlineData(DeleteJobs, """{"source":"","from":" ","to":null,"confirm":1}""", "one of 'source', 'from' and 'to' is needed")]
    [InlineData(DeleteJobs, """{"all":true,"confirm":1}""", "one of 'source', 'from' and 'to' is needed")]
    [InlineData(DeleteJobs, """{"from":"SECRET-CALLER-CONTENT"}""", "'from' is not a UTC time such as 2026-10-03T18:00:00Z or a date such as 2026-10-03")]
    [InlineData(DeleteJobs, """{"from":"03/10/2026"}""", "'from' is not a UTC time such as 2026-10-03T18:00:00Z or a date such as 2026-10-03")]
    [InlineData(DeleteJobs, """{"to":"yesterday"}""", "'to' is not a UTC time such as 2026-10-03T18:00:00Z or a date such as 2026-10-03")]
    [InlineData(DeleteJobs, """{"to":"2026-13-45"}""", "'to' is not a UTC time such as 2026-10-03T18:00:00Z or a date such as 2026-10-03")]
    [InlineData(DeleteJobs, """{"from":"2026-10-04","to":"2026-10-03","confirm":1}""", "'from' must be before 'to'")]
    [InlineData(DeleteJobs, """{"from":"2026-10-04","to":"2026-10-04","confirm":1}""", "'from' must be before 'to'")]
    [InlineData(DeleteJobs, """{"source":"x","confirm":0}""", "'confirm' must be the rows value of a dry run")]
    [InlineData(DeleteJobs, """{"source":"x","confirm":-1}""", "'confirm' must be the rows value of a dry run")]
    [InlineData(DeleteJobs, """{"source":"x","confirm":true}""", "'confirm' has the wrong JSON type")]
    [InlineData(DeleteJobs, """{"source":"x","confirm":"SECRET-CALLER-CONTENT"}""", "'confirm' has the wrong JSON type")]
    [InlineData(DeleteJobs, """{"source":5,"confirm":1}""", "'source' has the wrong JSON type")]
    [InlineData(DeleteJobs, """{"source":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","confirm":1}""", "'source' holds at most 64 characters")]
    public async Task WrongArguments_AnswerWithTheCorrectShapeAndDeleteNothing(string tool, string? arguments, string expectedProblem)
    {
        await using var app = new App();
        var client = app.CreateClient();
        await PrintAsync(app, client, "Title", source: "x");
        app.Logs.Entries.Clear();

        var (isError, text) = await client.CallToolAsync(tool, arguments);

        Assert.True(isError);
        Assert.Equal($"Wrong arguments for '{tool}': {expectedProblem}. Example of a valid call: {PrinterTools.ValidCallFor(tool)}", text);
        Assert.DoesNotContain(Secret, text);
        Assert.Equal((1, 1, 1), await app.RowCountsAsync());
        Assert.Empty(app.AuditLines());
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
    }

    [Fact]
    public async Task JournalOff_BothTools_SaySoAndDeleteNothing()
    {
        await using var app = new App(null, ("Journal:DataPath", ""));
        var client = app.CreateClient();
        await app.JournalIdleAsync();

        var (_, one) = await client.CallToolAsync(DeleteJob, Args(new { id = UnknownId }));
        var (_, dryRun) = await client.CallToolAsync(DeleteJobs, Args(new { source = "x" }));
        var (_, many) = await client.CallToolAsync(DeleteJobs, Args(new { source = "x", confirm = 1 }));

        Assert.All([one, dryRun, many], answer => Assert.Equal("Not deleted: The print journal is off", answer));
    }

    [Fact]
    public async Task StoreFails_SaysNotAvailableLogsOneWarningAndTheWriterGoesOn()
    {
        await using var app = new App(new FailingDeleteStore());
        var client = app.CreateClient();
        await app.JournalIdleAsync();
        app.Logs.Entries.Clear();

        var (isError, one) = await client.CallToolAsync(DeleteJob, Args(new { id = UnknownId }));
        var (_, many) = await client.CallToolAsync(DeleteJobs, Args(new { source = "x", confirm = 1 }));

        Assert.False(isError);
        Assert.Equal(NotAvailable, one);
        Assert.Equal(NotAvailable, many);
        // The fault text of the store goes to the log, never to the caller.
        Assert.DoesNotContain(Secret, one + many);
        var warnings = app.Logs.Entries.Where(entry => entry.Level >= LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.StartsWith($"Journal read failed: job {UnknownId}", warnings[0].Message);
        Assert.StartsWith("Journal read failed: a delete", warnings[1].Message);
        Assert.Empty(app.AuditLines());

        // The writer still runs: a barrier after the failed deletes is reached.
        await app.JournalIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    // The rows are gone, so the answer says "Deleted". The file that stays large is one Warning, and the writer goes on.
    [Fact]
    public async Task DeleteJob_TheFileCannotBeMadeSmaller_IsDeletedWithOneWarning()
    {
        await using var app = new App(new NoCompactStore());
        var client = app.CreateClient();
        await app.JournalIdleAsync();
        app.Logs.Entries.Clear();

        var (_, answer) = await client.CallToolAsync(DeleteJob, Args(new { id = UnknownId }));
        await app.JournalIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        AssertCounts(AnswerJson(answer, JournalTools.DeletedNotice), rows: 1, jobs: 1, reprints: 0);
        Assert.Single(app.AuditLines());
        var warning = Assert.Single(app.Logs.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal(JournalCategory, warning.Category);
        Assert.StartsWith("Journal delete: the database file did not get smaller. Later rows use its free pages.", warning.Message);
    }

    // The size limit of the journal is the size of its files. A delete gives the space back, and the journal stores again.
    [Fact]
    public async Task JournalFull_AfterADelete_HasSpaceAgainAndStoresTheNextJob()
    {
        const long Limit = 400_000;
        await using var app = new App(null, ("Journal:MaxDatabaseBytes", Limit.ToString()));
        var client = app.CreateClient();
        var text = string.Join('\n', Enumerable.Repeat(new string('x', 47), 200));
        var isFull = false;
        for (var i = 0; i < 100 && !isFull; i++)
        {
            await PrintAsync(app, client, text, source: "fill");
            isFull = app.Logs.Entries.Any(entry => entry.Category == JournalCategory && entry.Message.StartsWith("Journal is full: ", StringComparison.Ordinal));
        }

        Assert.True(isFull, "The journal did not get full.");
        var stored = (await app.RowCountsAsync()).Jobs;
        Assert.InRange(stored, 1, 99);
        var databasePath = Path.Combine(app.JournalDirectory, "journal.db");
        var fullSize = new FileInfo(databasePath).Length;
        Assert.InRange(fullSize, Limit, long.MaxValue);

        var (_, answer) = await client.CallToolAsync(DeleteJobs, Args(new { source = "fill", confirm = stored }));
        AssertCounts(AnswerJson(answer, JournalTools.DeletedNotice), rows: stored, jobs: stored, reprints: 0);
        Assert.Equal((0, 0, 0), await app.RowCountsAsync());

        // The file is smaller, and the write-ahead log holds nothing.
        Assert.InRange(new FileInfo(databasePath).Length, 1, fullSize / 2);
        var log = new FileInfo(databasePath + "-wal");
        Assert.True(!log.Exists || log.Length == 0);

        var next = await PrintAsync(app, client, "After the delete", source: "next");
        Assert.Equal("next", next.Source);
        Assert.Equal((1, 1, 1), await app.RowCountsAsync());
        Assert.Contains(app.Logs.Entries, entry => entry.Category == JournalCategory && entry.Message == "Journal has space again: prints are stored");
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.StartsWith("Journal delete: the database file did not get smaller", StringComparison.Ordinal));
    }

    // Deletes go through the one journal writer: a delete and the writes around it do not harm each other.
    [Fact]
    public async Task DeleteJobs_WhilePrintsArrive_LosesNoWriteAndLogsNoFault()
    {
        await using var app = new App();
        var client = app.CreateClient();
        const int Jobs = PrintJournal.MaxPendingJobs / 2;
        for (var i = 0; i < Jobs; i++)
            await PrintAsync(app, client, $"Old {i}", source: "old");
        app.Logs.Entries.Clear();

        var prints = Enumerable.Range(0, Jobs)
            .Select(i => client.SendJsonAsync(HttpMethod.Post, PrintUrl, Args(new { content = new object[] { new { type = "Text", content = $"New {i}" } }, source = "new" })))
            .ToList();
        var delete = client.CallToolAsync(DeleteJobs, Args(new { source = "old", confirm = Jobs }));
        var morePrints = Enumerable.Range(0, Jobs / 2)
            .Select(i => client.SendJsonAsync(HttpMethod.Post, PrintUrl, Args(new { content = new object[] { new { type = "Text", content = $"Later {i}" } }, source = "new" })))
            .ToList();
        await Task.WhenAll(prints.Concat(morePrints));
        var (_, answer) = await delete;

        AssertCounts(AnswerJson(answer, JournalTools.DeletedNotice), rows: Jobs, jobs: Jobs, reprints: 0);
        var rows = await app.JournalRowsAsync();
        Assert.Equal(Jobs + Jobs / 2, rows.Count);
        Assert.All(rows, row => Assert.Equal("new", row.Source));
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Level >= LogLevel.Warning);
        await using var db = app.JournalDb();
        Assert.Equal("ok", await db.Database.SqlQueryRaw<string>("PRAGMA integrity_check").ToListAsync().ContinueWith(task => task.Result.Single()));
    }

    // Owner decision: the journal over HTTP is read-only. Delete is for MCP, behind the key.
    [Fact]
    public async Task Http_NoEndpointDeletesOrChangesAJob()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var job = await PrintAsync(app, client, "Title");

        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.TrimStart('/').StartsWith("api/printer/jobs", StringComparison.OrdinalIgnoreCase) == true)
            .Select(endpoint => (
                Route: endpoint.RoutePattern.RawText!.TrimStart('/'),
                Methods: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []))
            .ToList();

        Assert.NotEmpty(routes);
        // Every route names its methods: a route with none would take any method.
        Assert.All(routes, route => Assert.NotEmpty(route.Methods));
        Assert.All(routes, route => Assert.All(route.Methods, method => Assert.Contains(method, new[] { "GET", "POST" })));
        // The one POST is the reprint.
        Assert.Equal(["api/printer/jobs/{id}/reprint"], routes.Where(route => route.Methods.Contains("POST")).Select(route => route.Route));

        foreach (var method in new[] { "DELETE", "PUT", "PATCH" })
        {
            foreach (var url in new[] { JobsUrl, $"{JobsUrl}/{job.Id}", $"{JobsUrl}/{job.Id}/reprint", $"{JobsUrl}?source=x" })
            {
                var (status, _) = await client.SendJsonAsync(new HttpMethod(method), url, "{}", authorization: TestHttp.McpAuthorization);
                Assert.Contains(status, new[] { HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound });
            }
        }

        Assert.Equal((1, 1, 1), await app.RowCountsAsync());
    }

    [Fact]
    public async Task Mcp_DeleteCallWithoutTheKey_Is401AndDeletesNothing()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var job = await PrintAsync(app, client, "Title", source: "x");
        var one = Args(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = DeleteJob, arguments = new { id = job.Id } } });
        var many = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"delete_jobs","arguments":{"source":"x","confirm":1}}}""";

        var (noKey, _) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, one);
        var (wrongKey, _) = await client.SendJsonAsync(HttpMethod.Post, TestHttp.McpUrl, many, authorization: "Bearer wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, noKey);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey);
        Assert.Equal((1, 1, 1), await app.RowCountsAsync());
        Assert.Empty(app.AuditLines());
    }
}
