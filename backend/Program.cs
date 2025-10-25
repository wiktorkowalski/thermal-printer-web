using ThermalPrinterWeb;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSingleton<IPrinterService, PrinterService>();

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
