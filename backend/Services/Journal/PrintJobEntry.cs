using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Journal;

// One journaled request, copied out of the HttpContext before the request ends.
internal sealed record PrintJobEntry
{
    internal const int MaxTitleLength = 100;
    internal const string ImageHashPrefix = "sha256:";

    // The same JSON as the HTTP API: camelCase names, enum names.
    internal static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // "1.0.0+<commit>" in the Docker image; no commit in a local build.
    internal static readonly string AppVersion =
        typeof(PrintJobEntry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public required PrintJobTrace Trace { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required TimeSpan Duration { get; init; }

    // What the endpoint is, for a request that did not get to PrintJobLog.
    public required string Transport { get; init; }
    public required int HttpStatus { get; init; }
    public required string? UserAgent { get; init; }
    public required string? RemoteIp { get; init; }
    public required Dictionary<string, string[]> Headers { get; init; }
    public required byte[]? Request { get; init; }

    // An exception that left the endpoint.
    public Exception? Fault { get; init; }

    // What the entry holds in memory while it waits for the writer.
    public long PendingBytes => (Request?.Length ?? 0) + (Trace.Bytes?.Length ?? 0);

    // Runs on the journal writer: it serializes the blocks, and that takes time for a photo.
    public PrintJob ToRow()
    {
        var outcome = Trace.Outcome;
        var content = outcome?.Content;
        var plainText = content is null ? null : PlainText(content);

        return new PrintJob
        {
            Id = Trace.Id,
            CreatedAt = CreatedAt,
            DurationMs = (long)Duration.TotalMilliseconds,
            Transport = outcome?.Transport ?? Transport,
            Source = Cut(outcome?.Source, PrintJobLog.MaxSourceLength),
            UserAgent = Cut(UserAgent, PrintJobLog.MaxUserAgentLength),
            RemoteIp = RemoteIp,
            Result = ResultOf(outcome?.Result, HttpStatus),
            Error = outcome?.Result.Error,
            HttpStatus = HttpStatus,
            Title = Cut(plainText?.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)), MaxTitleLength),
            BlockCount = content?.Count,
            ByteCount = Trace.Bytes?.Length,
            PaperDots = Trace.PaperDots,
            PrinterStatus = Trace.Status is null ? null : JsonSerializer.Serialize(Trace.Status, ApiJson),
            RequestBytes = Request?.Length ?? 0,
            AppVersion = AppVersion,
            Payload = new PrintJobPayload
            {
                JobId = Trace.Id,
                Request = Request,
                Bytes = Trace.Bytes,
                Blocks = content is null ? null : BlocksJson(content),
                Options = outcome?.Options is null ? null : JsonSerializer.Serialize(outcome.Options, ApiJson),
                PlainText = plainText,
                Headers = JsonSerializer.Serialize(Headers, ApiJson),
                Exception = (Fault ?? Trace.Exception)?.ToString(),
                Log = Trace.LogLines() is { Length: > 0 } lines ? string.Join('\n', lines) : null
            }
        };
    }

    // The explicit mapping keeps the stored numbers apart from the names and the order of PrintFailure.
    internal static JobResult ResultOf(PrintResult? result, int httpStatus) => result switch
    {
        { Success: true } => JobResult.Printed,
        { Failure: PrintFailure.Validation } => JobResult.Validation,
        { Failure: PrintFailure.Busy } => JobResult.Busy,
        { } => JobResult.Printer,
        // No print result: the request did not get to the print path. A 4xx is the caller's fault (JSON that does not bind, 415, 413).
        null => httpStatus is >= 400 and < 500 ? JobResult.Validation : JobResult.Fault
    };

    // The request body holds each picture already. A second copy of a photo is up to 22 MB.
    internal static string BlocksJson(List<PrintContent> content)
    {
        var blocks = JsonSerializer.SerializeToNode(content, ApiJson)!.AsArray();
        foreach (var (index, block) in content.Index())
        {
            if (block is { Type: ContentType.Image, Content.Length: > 0 })
                blocks[index]!["content"] = ImageHash(block.Content);
        }

        return blocks.ToJsonString(ApiJson);
    }

    // The hash of the base64 text as the caller sent it, so it matches the same text in the request body.
    internal static string ImageHash(string base64)
        => $"{ImageHashPrefix}{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(base64)))};chars={base64.Length}";

    private static string? PlainText(List<PrintContent> content)
    {
        var texts = content.Where(block => block is { Type: ContentType.Text, Content: not null }).Select(block => block.Content);
        var text = string.Join('\n', texts);
        return text.Length == 0 ? null : text;
    }

    private static string? Cut(string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        return text.Length > maxLength ? text[..maxLength] : text;
    }
}
