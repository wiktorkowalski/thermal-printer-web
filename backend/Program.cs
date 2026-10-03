using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel to listen on all interfaces (required for Docker)
var port = Environment.GetEnvironmentVariable("PORT") ?? "5160";
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(int.Parse(port)); // Listen on all interfaces
});

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    })
    // Model-binding failures answer in the same shape as every other print error.
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
            new BadRequestObjectResult(PrintResponse.FromModelState(context.ModelState));
    });
builder.Services.AddSingleton<IPrinterService, PrinterService>();
builder.Services.AddPrinterBlockHandlers();

// MCP server over HTTP at /mcp (stateless, no auth - single-user printer).
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();

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

app.MapControllers();
app.MapMcp("/mcp");

// Serve React app SPA (from wwwroot)
app.MapFallbackToFile("index.html");

app.Run();

// Lets the test project host the app (WebApplicationFactory<Program>).
public partial class Program;
