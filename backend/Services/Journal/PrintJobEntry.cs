using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ThermalPrinterWeb.Models;

namespace ThermalPrinterWeb.Services.Journal;

// One journaled request, copied out of the HttpContext before the request ends.
internal sealed record PrintJobEntry
{
    internal const int MaxTitleLength = 100;
    internal const string ImageHashPrefix = "sha256:";

    private const int HashChunkBytes = 4096;

    // The same JSON as the HTTP API: the web defaults of MVC plus ConfigureApiJson.
    internal static readonly JsonSerializerOptions ApiJson = CreateApiJson();

    // "1.0.0+<commit>" in the Docker image; no commit in a local build.
    internal static readonly string AppVersion =
        typeof(PrintJobEntry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public required PrintJobTrace Trace { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required TimeSpan Duration { get; init; }

    public required int HttpStatus { get; init; }
    public required string? UserAgent { get; init; }
    public required string? RemoteIp { get; init; }
    public required Dictionary<string, string[]> Headers { get; init; }
    public required byte[]? Request { get; init; }

    // ApiJson, with a hash in place of each picture.
    private static readonly JsonSerializerOptions BlocksJsonOptions = new(ApiJson)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { HashImageContent } }
    };

    // What Program.cs sets on the JSON options of the controllers. One place, so the stored blocks stay in the API form.
    internal static void ConfigureApiJson(JsonSerializerOptions options) => options.Converters.Add(new JsonStringEnumConverter());

    private static JsonSerializerOptions CreateApiJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ConfigureApiJson(options);
        return options;
    }

    // The serializer never writes the base64 text of a picture: no copy of up to 22 MB.
    private static void HashImageContent(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(PrintContent))
            return;

        var content = typeInfo.Properties.Single(property => property.Name == "content");
        content.Get = block => (PrintContent)block is { Type: ContentType.Image, Content: { Length: > 0 } base64 }
            ? ImageHash(base64)
            : ((PrintContent)block).Content;
    }

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
            // No outcome: the request did not get to PrintJobLog. Only the HTTP endpoint stores such a request.
            Transport = outcome?.Transport ?? PrintJobLog.HttpTransport,
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
            ReprintOf = Trace.ReprintOf,
            Payload = new PrintJobPayload
            {
                JobId = Trace.Id,
                Request = Request,
                Bytes = Trace.Bytes,
                Blocks = content is null ? null : BlocksJson(content),
                Options = outcome?.Options is null ? null : JsonSerializer.Serialize(outcome.Options, ApiJson),
                PlainText = plainText,
                Headers = JsonSerializer.Serialize(Headers, ApiJson),
                Exception = Trace.Exception?.ToString(),
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
    internal static string BlocksJson(List<PrintContent> content) => JsonSerializer.Serialize(content, BlocksJsonOptions);

    // The hash of the base64 text as the caller sent it (UTF-8), so it matches the same text in the request body.
    // In chunks: no second copy of the text.
    internal static string ImageHash(string base64)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encoder = Encoding.UTF8.GetEncoder();
        Span<byte> bytes = stackalloc byte[HashChunkBytes];
        var text = base64.AsSpan();
        while (!text.IsEmpty)
        {
            // A full buffer ends at a whole character: no surrogate pair is split.
            encoder.Convert(text, bytes, flush: true, out var charsUsed, out var bytesUsed, out _);
            hash.AppendData(bytes[..bytesUsed]);
            text = text[charsUsed..];
        }

        return $"{ImageHashPrefix}{Convert.ToHexStringLower(hash.GetHashAndReset())};chars={base64.Length}";
    }

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
