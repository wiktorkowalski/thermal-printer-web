using System.Text.Json.Serialization;
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
    });
builder.Services.AddSingleton<IPrinterService, PrinterService>();
builder.Services.AddPrinterBlockHandlers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add CORS for React frontend (development only)
if (builder.Environment.IsDevelopment())
{
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
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseStaticFiles();
app.UseRouting();

// Enable CORS in development only
if (app.Environment.IsDevelopment())
{
    app.UseCors("AllowReact");
}

app.MapControllers();

// Serve React app SPA (from wwwroot)
app.MapFallbackToFile("index.html");

app.Run();
