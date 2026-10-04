using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ThermalPrinterWeb.Controllers;
using ThermalPrinterWeb.Mcp;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Journal;
using ThermalPrinterWeb.Services.Printing;

// A JSON body that does not parse or bind: truncated JSON, a string where a number goes, an enum name that does not exist.
const string JsonPathError = "malformed JSON, wrong JSON type or unknown name";

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel to listen on all interfaces (required for Docker)
var port = Environment.GetEnvironmentVariable("PORT") ?? "5160";
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(int.Parse(port)); // Listen on all interfaces
    serverOptions.Limits.MaxRequestBodySize = PrinterService.MaxRequestBodyBytes;
});

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options => PrintJobEntry.ConfigureApiJson(options.JsonSerializerOptions))
    // Model-binding failures answer in the same shape as every other print error.
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            // A body that fails to parse also reports "request field is required": keep
            // only the JSON path entries ("$...") then, they hold the cause.
            var entries = context.ModelState.Where(entry => entry.Value is { Errors.Count: > 0 }).ToList();
            if (entries.Any(IsJsonPath))
                entries = entries.Where(IsJsonPath).ToList();

            // The System.Text.Json message for a JSON path names CLR types: own text in its place.
            var errors = entries.SelectMany(entry => entry.Value!.Errors.Select(error =>
                $"{entry.Key}: {(IsJsonPath(entry) ? JsonPathError : error.ErrorMessage)}".TrimStart(':', ' ')));
            return new BadRequestObjectResult(
                new PrintResponse(false, string.Join("; ", errors), PrintResponse.ValidationType));

            static bool IsJsonPath(KeyValuePair<string, ModelStateEntry?> entry)
                => entry.Key.StartsWith('$');
        };
    });
// After AddControllers: it registers the ProblemDetails factory.
builder.Services.Replace(ServiceDescriptor.Singleton<IClientErrorFactory, PrintResponseClientErrorFactory>());
builder.Services.AddOptions<PrinterOptions>()
    // Development starts from no printer, also when appsettings.Development.json is not found. The binding below sets an address given on purpose.
    // Keep Printer:Address out of appsettings.json: the binding would set it in Development too.
    .Configure<IHostEnvironment>((options, environment) => options.Address = environment.IsDevelopment() ? string.Empty : options.Address)
    .BindConfiguration(PrinterOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<PrinterOptions>, PrinterOptionsValidator>();
builder.Services.AddSingleton<IPrinterService, PrinterService>();
builder.Services.AddPrinterBlockHandlers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<PrintJobLog>();

// The print journal. A bad setting here turns the journal off; it never stops the app (PrintJournal).
builder.Services.AddOptions<JournalOptions>()
    .Configure<IConfiguration>((options, configuration) =>
        options.DataPath = configuration[JournalOptions.DataPathVariable] ?? options.DataPath)
    .BindConfiguration(JournalOptions.SectionName);
builder.Services.AddSingleton<JournalDatabase>();
builder.Services.AddSingleton<IPrintJournalStore, SqlitePrintJournalStore>();
builder.Services.AddSingleton<ContainerLayerCheck>(PrintJournal.IsInContainerLayer);
builder.Services.AddSingleton<PrintJournal>();
builder.Services.AddHostedService(services => services.GetRequiredService<PrintJournal>());
builder.Services.AddSingleton<ILoggerProvider, PrintJobTraceLoggerProvider>();
builder.Services.AddSingleton(services => new PrintJournalReader(
    services.GetRequiredService<JournalDatabase>(), services.GetRequiredService<PrintJournal>()));
builder.Services.AddSingleton<PrintJobReprinter>();
builder.Services.AddSingleton(services => new PrintJobDeleter(
    services.GetRequiredService<PrintJournal>(),
    services.GetRequiredService<PrintJournalReader>(),
    services.GetRequiredService<PrintJobLog>(),
    services.GetRequiredService<ILogger<PrintJobDeleter>>()));

// MCP server over HTTP at /mcp (stateless). McpApiKeyMiddleware guards it with a bearer token.
builder.Services.AddOptions<McpAuthOptions>().BindConfiguration(McpAuthOptions.SectionName);
builder.Services.AddMcpServer(options => options.ServerInstructions = PrinterTools.ServerInstructions)
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly()
    .WithRequestFilters(filters => filters.AddCallToolFilter(ArgumentShapeFilter.Wrap));

// Development only: Swagger (it lists every endpoint) and CORS for the React dev server.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowReact", policy =>
        {
            policy.WithOrigins("http://localhost:5173")
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
    });
}

var app = builder.Build();

PrinterOptions printerOptions;
try
{
    printerOptions = app.Services.GetRequiredService<IOptions<PrinterOptions>>().Value;
}
// Also a value that does not bind, for example a ConnectTimeout that is not a time.
catch (Exception ex) when (ex is OptionsValidationException or InvalidOperationException)
{
    // Exit with a code: an unhandled exception leaves the process alive when it is PID 1 in the container.
    // Not the logger: it writes on a background thread, and the process ends now.
    Console.Error.WriteLine($"Printer settings are not valid, the app does not start: {ex.Message}");
    await app.DisposeAsync();
    return 1;
}

if (printerOptions.ResolveEndpoint() is { } printerEndpoint)
    app.Logger.LogInformation("Printer target: {Address}", printerEndpoint);
else
    app.Logger.LogInformation("Printer target: no printer ({Environment}); print and beep send nothing", app.Environment.EnvironmentName);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

// Enable CORS in development only
if (app.Environment.IsDevelopment())
{
    app.UseCors("AllowReact");
}

// After routing: it reads the endpoint. Before the journal: a request without the key gets no journal row.
app.UseMiddleware<McpApiKeyMiddleware>();

// After routing: the middleware reads the endpoint to know which requests are print jobs.
app.UseMiddleware<PrintJournalMiddleware>();

app.MapControllers();
app.MapMcp(McpApiKeyMiddleware.McpPath).WithMetadata(new McpEndpointAttribute(), new JournaledAttribute { JobsOnly = true });

// Serve React app SPA (from wwwroot)
app.MapFallbackToFile("index.html");

app.Run();
return 0;
