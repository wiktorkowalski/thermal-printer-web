using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ThermalPrinterWeb.Controllers;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services.Journal;

namespace ThermalPrinterWeb.Tests;

// The statistics, the search and the papercut ledger, through the real pipeline. The endpoints are public:
// these tests pin what an answer holds and what one call can cost.
public sealed class JournalQueryTests
{
    private const string Secret = TestBlocks.Secret;
    private const string PrintUrl = "/api/printer";
    private const string JobsUrl = "/api/printer/jobs";
    private const string StatsUrl = JobsUrl + "/stats";
    private const string SearchUrl = JobsUrl + "/search";
    private const string PapercutsUrl = JobsUrl + "/papercuts";

    // A fake printer. "searchBudget" replaces the time one search call may take.
    private sealed class App(TimeSpan? searchBudget = null) : TestApp(Production)
    {
        public RecordingPrinter Printer { get; } = new();

        protected override void ConfigurePrinter(IWebHostBuilder builder)
        {
            UseRecordingPrinter(builder, Printer);
            if (searchBudget is { } budget)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<PrintJournalReader>();
                    services.AddSingleton(provider => new PrintJournalReader(
                        provider.GetRequiredService<JournalDatabase>(), provider.GetRequiredService<PrintJournal>(), budget));
                });
            }
        }
    }

    private static string TextJob(string text, string? source = null)
        => JsonSerializer.Serialize(new { content = new object[] { new { type = "Text", content = text } }, source });

    private static async Task<PrintJob> PrintAsync(TestApp app, HttpClient client, string text, string? source = null)
    {
        var (status, _) = await client.SendJsonAsync(HttpMethod.Post, PrintUrl, TextJob(text, source));
        Assert.Equal(HttpStatusCode.OK, status);
        return (await app.JournalRowsAsync())[^1];
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var (status, body) = await client.SendJsonAsync(HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(body).RootElement;
    }

    private static Task<JsonElement> SearchAsync(HttpClient client, string query, string extra = "")
        => GetJsonAsync(client, $"{SearchUrl}?q={Uri.EscapeDataString(query)}{extra}");

    private static List<Guid> HitIds(JsonElement result)
        => [.. result.GetProperty("hits").EnumerateArray().Select(hit => hit.GetProperty("job").GetProperty("id").GetGuid())];

    private static PrintJob Row(
        DateTime createdAt, JobResult result = JobResult.Printed, string? source = null, int? paperDots = null,
        Guid? reprintOf = null, string? text = null)
    {
        var id = Guid.CreateVersion7(new DateTimeOffset(createdAt, TimeSpan.Zero));
        return new PrintJob
        {
            Id = id,
            CreatedAt = createdAt,
            Transport = "http",
            Source = source,
            Result = result,
            HttpStatus = 200,
            PaperDots = paperDots,
            ReprintOf = reprintOf,
            Title = text?.Split('\n')[0],
            BlockCount = text is null ? null : 1,
            AppVersion = "test",
            Payload = new PrintJobPayload { JobId = id, Headers = "{}", PlainText = text },
            Text = text is null ? null : new PrintJobText { JobId = id, Text = text }
        };
    }

    private static async Task AddRowsAsync(TestApp app, params PrintJob[] rows)
    {
        await app.JournalIdleAsync();
        await using var db = app.JournalDb();
        db.PrintJobs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    // Noon of a UTC day: "daysAgo" 0 is today.
    private static DateTime Noon(int daysAgo) => DateTime.UtcNow.Date.AddDays(-daysAgo).AddHours(12);

    [Fact]
    public async Task Stats_SeededJournal_CountsJobsPrintsReprintsAndPaper()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var first = Row(Noon(0), source: "a", paperDots: 100);
        await AddRowsAsync(
            app,
            first,
            Row(Noon(0).AddMinutes(1), source: "a", paperDots: 200),
            // A rejected job used no paper.
            Row(Noon(0).AddMinutes(2), JobResult.Validation, source: "a", paperDots: 50),
            Row(Noon(0).AddMinutes(3), paperDots: 100, reprintOf: first.Id),
            Row(Noon(1), source: "b", paperDots: 80),
            Row(Noon(1).AddMinutes(1), JobResult.Printer, source: "b"),
            // Before the default 30 days.
            Row(Noon(40), source: "old", paperDots: 1000));

        var stats = await GetJsonAsync(client, StatsUrl);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(today.AddDays(-29).ToString("yyyy-MM-dd"), stats.GetProperty("from").GetString());
        Assert.Equal(today.ToString("yyyy-MM-dd"), stats.GetProperty("to").GetString());
        Assert.Equal("""{"jobs":6,"printed":4,"reprints":1,"paperDots":480}""", stats.GetProperty("totals").GetRawText());

        var byDay = stats.GetProperty("byDay");
        Assert.Equal(PrintJournalReader.DefaultStatsDays, byDay.GetArrayLength());
        Assert.Equal($$"""{"day":"{{today:yyyy-MM-dd}}","jobs":4,"printed":3,"paperDots":400}""", byDay[29].GetRawText());
        Assert.Equal($$"""{"day":"{{today.AddDays(-1):yyyy-MM-dd}}","jobs":2,"printed":1,"paperDots":80}""", byDay[28].GetRawText());
        Assert.Equal($$"""{"day":"{{today.AddDays(-2):yyyy-MM-dd}}","jobs":0,"printed":0,"paperDots":0}""", byDay[27].GetRawText());

        Assert.Equal(
            """[{"source":"a","jobs":3,"printed":2,"paperDots":300},{"source":"b","jobs":2,"printed":1,"paperDots":80},{"source":null,"jobs":1,"printed":1,"paperDots":100}]""",
            stats.GetProperty("bySource").GetRawText());
        Assert.False(stats.GetProperty("moreSources").GetBoolean());
        Assert.Equal(
            """[{"result":"Printed","jobs":4},{"result":"Validation","jobs":1},{"result":"Printer","jobs":1}]""",
            stats.GetProperty("byResult").GetRawText());

        var year = await GetJsonAsync(client, $"{StatsUrl}?days=365");
        Assert.Equal(7, year.GetProperty("totals").GetProperty("jobs").GetInt32());
        Assert.Equal(1480, year.GetProperty("totals").GetProperty("paperDots").GetInt64());
    }

    [Theory]
    [InlineData("?days=0", 1)]
    [InlineData("?days=-3", 1)]
    [InlineData("?days=7", 7)]
    [InlineData("?days=100000", PrintJournalReader.MaxStatsDays)]
    public async Task Stats_Days_StayInsideTheCap(string query, int expectedDays)
    {
        await using var app = new App();
        var client = app.CreateClient();
        await app.JournalIdleAsync();

        var stats = await GetJsonAsync(client, StatsUrl + query);

        Assert.Equal(expectedDays, stats.GetProperty("byDay").GetArrayLength());
        Assert.Equal(0, stats.GetProperty("totals").GetProperty("jobs").GetInt32());
        Assert.Equal(0, stats.GetProperty("bySource").GetArrayLength());
        Assert.Equal(0, stats.GetProperty("byResult").GetArrayLength());
    }

    // "source" is caller text: a caller that makes up names must not make the answer grow.
    [Fact]
    public async Task Stats_ManySources_ServesTheLargestOnly()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var rows = Enumerable.Range(0, PrintJournalReader.MaxStatsSources + 5)
            .Select(index => Row(Noon(0).AddSeconds(index), source: $"source-{index:00}"))
            .Append(Row(Noon(0).AddMinutes(5), source: "source-07"))
            .ToArray();
        await AddRowsAsync(app, rows);

        var stats = await GetJsonAsync(client, StatsUrl);

        var sources = stats.GetProperty("bySource");
        Assert.Equal(PrintJournalReader.MaxStatsSources, sources.GetArrayLength());
        Assert.True(stats.GetProperty("moreSources").GetBoolean());
        Assert.Equal("source-07", sources[0].GetProperty("source").GetString());
        Assert.Equal(2, sources[0].GetProperty("jobs").GetInt32());
        Assert.Equal(rows.Length, stats.GetProperty("totals").GetProperty("jobs").GetInt32());
    }

    [Fact]
    public async Task Stats_DaysThatDoNotBind_Answers400()
    {
        await using var app = new App();
        var client = app.CreateClient();

        var (status, body) = await client.SendJsonAsync(HttpMethod.Get, $"{StatsUrl}?days=abc");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(PrintResponse.ValidationType, JsonDocument.Parse(body).RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Search_Hit_AnswersTheJobWithASnippetOnOneLine()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        await PrintAsync(app, client, "Shopping\nMilk and bread\nEggs", source: "web/note");
        await PrintAsync(app, client, "Something else");

        var result = await SearchAsync(client, "BREAD");

        var hit = Assert.Single(result.GetProperty("hits").EnumerateArray());
        Assert.Equal("Shopping Milk and bread Eggs", hit.GetProperty("snippet").GetString());
        Assert.Equal("Shopping", hit.GetProperty("job").GetProperty("title").GetString());
        Assert.Equal("web/note", hit.GetProperty("job").GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task Search_Miss_AnswersNoHit()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        await PrintAsync(app, client, "Milk and bread");

        var result = await SearchAsync(client, "butter");

        Assert.Equal(0, result.GetProperty("hits").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("next").ValueKind);
    }

    // The query is plain text: no character in it is a pattern, and none of it gets into SQL.
    [Theory]
    [InlineData("0%", "100% sure")]
    [InlineData("a_b", "a_b")]
    [InlineData("%%", null)]
    [InlineData("__", null)]
    [InlineData("a%b", null)]
    [InlineData("\\%", null)]
    [InlineData("'; DROP TABLE PrintJobs; --", null)]
    [InlineData("\" OR 1=1", null)]
    [InlineData("ŻÓŁĆ", "zażółć gęślą jaźń")]
    public async Task Search_PatternAndSqlCharacters_AreLiteralText(string query, string? expectedTitle)
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        foreach (var text in new[] { "100% sure", "a_b", "axb", "zażółć gęślą jaźń" })
            await PrintAsync(app, client, text);

        var result = await SearchAsync(client, query);

        string?[] expected = expectedTitle is null ? [] : [expectedTitle];
        Assert.Equal(expected, result.GetProperty("hits").EnumerateArray().Select(hit => hit.GetProperty("job").GetProperty("title").GetString()));
        Assert.Equal(4, (await app.JournalRowsAsync()).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?q=")]
    [InlineData("?q=a")]
    [InlineData("?q=%20%20a%20%20")]
    [InlineData("?q=" + "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789x")]
    public async Task Search_QueryTooShortOrTooLong_Answers400(string query)
    {
        await using var app = new App();
        var client = app.CreateClient();
        await app.JournalIdleAsync();

        var (status, body) = await client.SendJsonAsync(HttpMethod.Get, SearchUrl + query);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var response = JsonDocument.Parse(body).RootElement;
        Assert.Equal(PrintResponse.ValidationType, response.GetProperty("type").GetString());
        Assert.Equal(PrintJobsController.InvalidQueryError, response.GetProperty("error").GetString());
        Assert.Equal("q must hold 2 to 100 characters", PrintJobsController.InvalidQueryError);
    }

    [Fact]
    public async Task Search_QueryOfTheMaxLength_IsRead()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var text = new string('q', PrintJournalReader.MaxQueryLength);
        await AddRowsAsync(app, Row(Noon(0), text: $"before {text} after"));

        var result = await SearchAsync(client, text);

        Assert.Single(result.GetProperty("hits").EnumerateArray());
    }

    [Fact]
    public async Task Search_BadCursor_Answers400()
    {
        await using var app = new App();
        var client = app.CreateClient();

        var (status, body) = await client.SendJsonAsync(HttpMethod.Get, $"{SearchUrl}?q=text&before=not-an-id");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(PrintJobsController.InvalidCursorError, JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Search_Pages_NewestFirstWithNoHitTwice()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var rows = Enumerable.Range(0, 7)
            .Select(index => Row(Noon(1).AddSeconds(index), text: index % 2 == 0 ? $"match {index}" : $"other {index}"))
            .ToArray();
        await AddRowsAsync(app, rows);
        var expected = rows.Where(row => row.Text!.Text.StartsWith("match", StringComparison.Ordinal)).Select(row => row.Id).Reverse().ToList();

        var seen = new List<Guid>();
        var extra = "&limit=2";
        var pages = 0;
        do
        {
            var page = await SearchAsync(client, "match", extra);
            Assert.InRange(page.GetProperty("hits").GetArrayLength(), 0, 2);
            seen.AddRange(HitIds(page));
            extra = page.GetProperty("next").GetString() is { } next ? $"&limit=2&before={next}" : "";
            pages++;
        }
        while (extra.Length > 0 && pages < 10);

        Assert.Equal(expected, seen);
        Assert.InRange(pages, 2, 3);
    }

    [Theory]
    [InlineData("&limit=1000", PrintJournalReader.MaxPageSize)]
    [InlineData("&limit=-1", 1)]
    [InlineData("", PrintJournalReader.DefaultPageSize)]
    public async Task Search_Limit_StaysInsideTheCap(string extra, int expected)
    {
        await using var app = new App();
        var client = app.CreateClient();
        await AddRowsAsync(app, [.. Enumerable.Range(0, PrintJournalReader.MaxPageSize + 5).Select(index => Row(Noon(1).AddSeconds(index), text: "match"))]);

        var page = await SearchAsync(client, "match", extra);

        Assert.Equal(expected, page.GetProperty("hits").GetArrayLength());
        Assert.Equal(JsonValueKind.String, page.GetProperty("next").ValueKind);
    }

    // One call has a time budget. Past it the answer holds what was found and where to go on:
    // the caller gets every hit with more calls, and no call reads the whole journal.
    [Fact]
    public async Task Search_PastItsTimeBudget_StopsAfterOneBatchAndSaysWhereToGoOn()
    {
        await using var app = new App(searchBudget: TimeSpan.Zero);
        var client = app.CreateClient();
        const int Rows = PrintJournalReader.SearchBatchRows * 2 + 50;
        var rows = Enumerable.Range(0, Rows)
            .Select(index => Row(Noon(1).AddSeconds(index), text: index == 0 ? "the oldest strip has the needle" : $"hay {index}"))
            .ToArray();
        await AddRowsAsync(app, rows);

        var first = await SearchAsync(client, "needle");
        Assert.Equal(0, first.GetProperty("hits").GetArrayLength());
        // The last id of the first batch.
        Assert.Equal(rows[Rows - PrintJournalReader.SearchBatchRows].Id, first.GetProperty("next").GetGuid());

        var second = await SearchAsync(client, "needle", $"&before={first.GetProperty("next").GetString()}");
        Assert.Equal(0, second.GetProperty("hits").GetArrayLength());
        var third = await SearchAsync(client, "needle", $"&before={second.GetProperty("next").GetString()}");

        Assert.Equal([rows[0].Id], HitIds(third));
        Assert.Equal(JsonValueKind.Null, third.GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task Search_LongJournal_ReadsItAllInOneCallInsideTheBudget()
    {
        await using var app = new App();
        var client = app.CreateClient();
        var rows = Enumerable.Range(0, PrintJournalReader.SearchBatchRows * 3)
            .Select(index => Row(Noon(1).AddSeconds(index), text: index == 0 ? "needle" : "hay"))
            .ToArray();
        await AddRowsAsync(app, rows);

        var result = await SearchAsync(client, "needle");

        Assert.Equal([rows[0].Id], HitIds(result));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("next").ValueKind);
    }

    // A reprint row is a reference: the hit is the first job, once.
    [Fact]
    public async Task Search_ReprintedJob_IsOneHit()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        var original = await PrintAsync(app, client, "Printed twice");
        var (status, _) = await client.SendJsonAsync(HttpMethod.Post, $"{JobsUrl}/{original.Id}/reprint");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, (await app.JournalRowsAsync()).Count);

        var result = await SearchAsync(client, "twice");

        Assert.Equal([original.Id], HitIds(result));
    }

    // Row text is caller text: it leaves as JSON data, with no change. The query goes to no log, and a read stores no row.
    [Fact]
    public async Task Search_HostileTextAndQuery_StayDataAndOutOfTheLog()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        const string hostile = "<script>alert(1)</script>\"'&</textarea>";
        await PrintAsync(app, client, $"note {Secret} {hostile}", source: "<img src=x onerror=1>");
        app.Logs.Entries.Clear();

        using var response = await client.GetAsync($"{SearchUrl}?q={Uri.EscapeDataString(Secret + " <script>")}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        Assert.True(response.Headers.CacheControl?.NoStore);
        var hit = Assert.Single(JsonDocument.Parse(body).RootElement.GetProperty("hits").EnumerateArray());
        Assert.Equal($"note {Secret} {hostile}", hit.GetProperty("snippet").GetString());
        Assert.Equal("<img src=x onerror=1>", hit.GetProperty("job").GetProperty("source").GetString());
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Message.Contains(Secret));
        Assert.Single(await app.JournalRowsAsync());
    }

    [Theory]
    [InlineData(StatsUrl)]
    [InlineData(SearchUrl + "?q=text")]
    [InlineData(PapercutsUrl)]
    public async Task Queries_WhileTheMaxNumberRuns_Answer503BusyWithRetryAfter(string url)
    {
        await using var app = new App();
        var client = app.CreateClient();
        await app.JournalIdleAsync();
        var reader = app.Services.GetRequiredService<PrintJournalReader>();
        for (var i = 0; i < PrintJournalReader.MaxQueries; i++)
            Assert.True(reader.TryBeginQuery());
        // The start of a host logs a Warning on a machine with no built frontend.
        app.Logs.Entries.Clear();

        using var busy = await client.GetAsync(url);
        reader.EndQuery();
        using var after = await client.GetAsync(url);
        // The plain list is not behind the gate.
        using var list = await client.GetAsync(JobsUrl);
        reader.EndQuery();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal("5", Assert.Single(busy.Headers.GetValues("Retry-After")));
        var response = JsonDocument.Parse(await busy.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(PrintResponse.BusyType, response.GetProperty("type").GetString());
        Assert.Equal(JournalFault.Busy.Error, response.GetProperty("error").GetString());
        // The gate is free again after a query that ran.
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public void Snippet_LongText_IsShortAndKeepsTheMatch()
    {
        var text = new string('a', 500) + "\n\n\t needle \r\n" + new string('b', 500);

        var snippet = PrintJournalReader.Snippet(text, text.IndexOf("needle", StringComparison.Ordinal));

        Assert.InRange(snippet.Length, 1, PrintJournalReader.MaxSnippetLength);
        Assert.StartsWith("…a", snippet);
        Assert.EndsWith("b…", snippet);
        Assert.Contains("a needle b", snippet);
        Assert.DoesNotContain(snippet, character => char.IsControl(character));
    }

    // An emoji is two UTF-16 units: a cut in its middle would leave a text that JSON cannot hold.
    [Fact]
    public void Snippet_EmojiAtBothEnds_KeepsWholeCharacters()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 300));

        for (var at = 0; at < 200; at++)
        {
            var snippet = PrintJournalReader.Snippet(text, at);

            Assert.InRange(snippet.Length, 1, PrintJournalReader.MaxSnippetLength);
            Assert.Equal(snippet, JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(snippet)));
            // The replacement character: what the JSON writer puts in place of half a pair.
            Assert.False(snippet.Contains((char)0xFFFD));
        }
    }

    [Theory]
    [InlineData("PAPERCUT\nzsh eats an unquoted glob", "zsh eats an unquoted glob")]
    [InlineData("PAPERCUT x3\n\n  zsh   eats an\tunquoted glob  \nmore", "zsh eats an unquoted glob")]
    [InlineData("papercut X12\n----------\nthe hook rejects a chain", "the hook rejects a chain")]
    [InlineData("PAPERCUT: docker build is slow\nbody", "docker build is slow")]
    [InlineData("PAPERCUT x2 - docker build is slow", "docker build is slow")]
    [InlineData("PAPERCUT", "PAPERCUT")]
    [InlineData("PAPERCUTS\nplural is another header", null)]
    [InlineData("PAPERCUTTER SALE\nscissors", null)]
    [InlineData("TWOJA KOLEJ\nPAPERCUT", null)]
    [InlineData("", null)]
    public void PapercutSubject_FirstLine_DecidesAndTheSubjectFollows(string text, string? expected)
        => Assert.Equal(expected, PrintJournalReader.PapercutSubject(text));

    [Fact]
    public void PapercutSubject_LongLine_IsCut()
    {
        var subject = PrintJournalReader.PapercutSubject("PAPERCUT\n" + new string('x', 500));

        Assert.Equal(PrintJournalReader.MaxSubjectLength, subject!.Length);
    }

    [Fact]
    public async Task Papercuts_GroupsTheStripsBySubjectAndLeavesOutEveryOtherJob()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        var oldest = await PrintAsync(app, client, "PAPERCUT\nzsh eats an unquoted glob\nfirst time");
        // The header in small letters is the header too.
        await PrintAsync(app, client, "papercut: docker build is slow");
        await PrintAsync(app, client, "PAPERCUT x2\nZSH eats an unquoted glob\nsecond time");
        await PrintAsync(app, client, "Shopping\nPAPERCUT is not the first line");
        await PrintAsync(app, client, "PAPERCUTTER SALE\nscissors");
        var newest = await PrintAsync(app, client, "PAPERCUT x3\nzsh eats an unquoted GLOB");
        // A reprint is not a new papercut, and a strip that did not print is none.
        Assert.Equal(HttpStatusCode.OK, (await client.SendJsonAsync(HttpMethod.Post, $"{JobsUrl}/{newest.Id}/reprint")).Status);
        await AddRowsAsync(app, Row(DateTime.UtcNow.AddMinutes(1), JobResult.Printer, text: "PAPERCUT\nzsh eats an unquoted glob"));

        var ledger = await GetJsonAsync(client, PapercutsUrl);

        Assert.Equal(4, ledger.GetProperty("strips").GetInt32());
        Assert.False(ledger.GetProperty("more").GetBoolean());
        var papercuts = ledger.GetProperty("papercuts");
        Assert.Equal(2, papercuts.GetArrayLength());
        Assert.Equal("zsh eats an unquoted GLOB", papercuts[0].GetProperty("subject").GetString());
        Assert.Equal(3, papercuts[0].GetProperty("count").GetInt32());
        Assert.Equal(newest.Id, papercuts[0].GetProperty("lastJobId").GetGuid());
        Assert.Equal(oldest.CreatedAt, papercuts[0].GetProperty("firstAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(newest.CreatedAt, papercuts[0].GetProperty("lastAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        Assert.EndsWith("Z", papercuts[0].GetProperty("lastAt").GetString());
        Assert.Equal("docker build is slow", papercuts[1].GetProperty("subject").GetString());
        Assert.Equal(1, papercuts[1].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Papercuts_EmptyJournal_AnswersAnEmptyLedger()
    {
        await using var app = new App();
        var client = app.CreateClient();
        await app.JournalIdleAsync();

        var ledger = await GetJsonAsync(client, PapercutsUrl);

        Assert.Equal("""{"papercuts":[],"strips":0,"more":false}""", ledger.GetRawText());
    }

    // More subjects than the ledger serves: the answer has its cap and says that it is cut.
    [Fact]
    public async Task Papercuts_ManySubjects_ServesTheCapAndSaysMore()
    {
        await using var app = new App();
        var client = app.CreateClient();
        await AddRowsAsync(app, [.. Enumerable.Range(0, PrintJournalReader.MaxPapercuts + 3)
            .Select(index => Row(Noon(1).AddSeconds(index), text: $"PAPERCUT\nsubject {index}"))]);

        var ledger = await GetJsonAsync(client, PapercutsUrl);

        Assert.Equal(PrintJournalReader.MaxPapercuts, ledger.GetProperty("papercuts").GetArrayLength());
        Assert.Equal(PrintJournalReader.MaxPapercuts + 3, ledger.GetProperty("strips").GetInt32());
        Assert.True(ledger.GetProperty("more").GetBoolean());
    }

    // A new print fills the text table that the search reads: the start of the text only.
    [Fact]
    public async Task Print_LongText_StoresTheStartForTheSearch()
    {
        await using var app = new NoPrinterApp();
        var client = app.CreateClient();
        var line = new string('x', 47);
        var blocks = Enumerable.Range(0, 3).Select(_ => new { type = "Text", content = string.Join('\n', Enumerable.Repeat(line, 100)) });
        await client.SendJsonAsync(HttpMethod.Post, PrintUrl, JsonSerializer.Serialize(new { content = blocks }));
        var job = Assert.Single(await app.JournalRowsAsync());
        Assert.Equal(JobResult.Printed, job.Result);

        await using var db = app.JournalDb();
        var text = await db.PrintJobTexts.SingleAsync();

        Assert.Equal(job.Id, text.JobId);
        Assert.Equal(PrintJobEntry.MaxSearchTextLength, text.Text.Length);
        Assert.True(job.Payload.PlainText!.Length > PrintJobEntry.MaxSearchTextLength);
        Assert.StartsWith(text.Text, job.Payload.PlainText);
    }

    [Fact]
    public void CutAtCharacter_EmojiAtTheCut_KeepsWholeCharacters()
    {
        Assert.Equal("ab", PrintJobEntry.CutAtCharacter("ab😀", 3));
        Assert.Equal("ab😀", PrintJobEntry.CutAtCharacter("ab😀", 4));
        Assert.Equal("abc", PrintJobEntry.CutAtCharacter("abc", 3));
    }

    // A database from before the text table, with rows: the app adds the table, fills it from the stored text
    // and keeps every row. Production has such a database.
    [Fact]
    public async Task Start_DatabaseFromBeforeTheTextTable_KeepsItsRowsAndFindsTheirText()
    {
        const string OldId = "01999999-0000-7000-8000-000000000001";
        const string ReprintId = "01999999-0000-7000-8000-000000000002";
        const string RejectedId = "01999999-0000-7000-8000-000000000003";
        var directory = TestApp.NewJournalDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var options = new DbContextOptionsBuilder<JournalDbContext>()
                .UseSqlite(JournalDbContext.ConnectionString(Path.Combine(directory, JournalOptions.DatabaseFileName)))
                .Options;
            await using (var old = new JournalDbContext(options))
            {
                await old.GetService<IMigrator>().MigrateAsync("ReprintOf");
                await old.Database.ExecuteSqlAsync(
                    $"INSERT INTO PrintJobs (Id, CreatedAt, DurationMs, Transport, Source, Result, HttpStatus, Title, BlockCount, PaperDots, RequestBytes, AppVersion) VALUES ({OldId}, '2026-10-01 12:00:00', 1, 'http', 'old-source', 0, 200, 'PAPERCUT', 1, 240, 0, 'test')");
                await old.Database.ExecuteSqlAsync(
                    $"INSERT INTO PrintJobPayloads (JobId, Headers, Blocks, PlainText) VALUES ({OldId}, {"{}"}, {"""[{"type":"Text","content":"PAPERCUT\nstary pasek żółć"}]"""}, {"PAPERCUT\nstary pasek żółć"})");
                await old.Database.ExecuteSqlAsync(
                    $"INSERT INTO PrintJobs (Id, CreatedAt, DurationMs, Transport, Result, HttpStatus, Title, BlockCount, RequestBytes, AppVersion, ReprintOf) VALUES ({ReprintId}, '2026-10-01 12:01:00', 1, 'http:reprint', 0, 200, 'PAPERCUT', 1, 0, 'test', {OldId})");
                await old.Database.ExecuteSqlAsync($"INSERT INTO PrintJobPayloads (JobId, Headers) VALUES ({ReprintId}, {"{}"})");
                await old.Database.ExecuteSqlAsync(
                    $"INSERT INTO PrintJobs (Id, CreatedAt, DurationMs, Transport, Result, HttpStatus, RequestBytes, AppVersion) VALUES ({RejectedId}, '2026-10-01 12:02:00', 1, 'http', 1, 400, 0, 'test')");
                await old.Database.ExecuteSqlAsync($"INSERT INTO PrintJobPayloads (JobId, Headers, PlainText) VALUES ({RejectedId}, {"{}"}, {""})");
            }

            await using var app = new NoPrinterApp { JournalDirectory = directory };
            var client = app.CreateClient();
            var added = await PrintAsync(app, client, "nowy pasek żółć");

            var rows = await app.JournalRowsAsync();
            Assert.Equal([Guid.Parse(OldId), Guid.Parse(ReprintId), Guid.Parse(RejectedId), added.Id], rows.Select(row => row.Id));
            Assert.Equal("old-source", rows[0].Source);
            Assert.Equal(240, rows[0].PaperDots);
            Assert.Equal("PAPERCUT\nstary pasek żółć", rows[0].Payload.PlainText);
            await using (var db = app.JournalDb())
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                // The old job with text and the new job; the reprint and the job with no text have no text row.
                Assert.Equal(
                    [Guid.Parse(OldId), added.Id],
                    await db.PrintJobTexts.OrderBy(text => text.JobId).Select(text => text.JobId).ToListAsync());
            }

            var list = await GetJsonAsync(client, JobsUrl);
            Assert.Equal(4, list.GetProperty("jobs").GetArrayLength());
            var found = await SearchAsync(client, "ŻÓŁĆ");
            Assert.Equal([added.Id, Guid.Parse(OldId)], HitIds(found));
            var ledger = await GetJsonAsync(client, PapercutsUrl);
            Assert.Equal("stary pasek żółć", ledger.GetProperty("papercuts")[0].GetProperty("subject").GetString());
            Assert.Equal(1, ledger.GetProperty("papercuts")[0].GetProperty("count").GetInt32());
            // The old job prints again from its stored blocks.
            var (reprint, _) = await client.SendJsonAsync(HttpMethod.Post, $"{JobsUrl}/{OldId}/reprint");
            Assert.Equal(HttpStatusCode.OK, reprint);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
