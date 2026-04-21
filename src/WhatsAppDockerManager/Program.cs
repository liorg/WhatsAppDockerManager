using Serilog;
using WhatsAppDockerManager.Configuration;
using WhatsAppDockerManager.Services;

DotNetEnv.Env.Load();
var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/whatsapp-manager-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Load configuration
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

// Register services
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));

// Core services
builder.Services.AddSingleton<ISupabaseService, SupabaseService>();
builder.Services.AddSingleton<IDockerService, DockerService>();
builder.Services.AddSingleton<IContainerManager, ContainerManager>();

// HTTP Client Factory for outgoing requests
builder.Services.AddHttpClient();

// Controllers
//builder.Services.AddControllers();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = 
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() 
    { 
        Title = "WhatsApp Docker Manager API", 
        Version = "v2.0",
        Description = @"
## WhatsApp Docker Manager API

### Phones
- `GET /api/phones` - רשימת כל הטלפונים
- `GET /api/phones/{phoneId}` - פרטי טלפון

### Send Messages (מעטפת - Docker לא חשוף)
- `POST /api/phones/{phoneId}/send/text` - שליחת טקסט
- `POST /api/phones/{phoneId}/send/buttons` - שליחת כפתורים
- `POST /api/phones/{phoneId}/send/list` - שליחת תפריט
- `POST /api/phones/{phoneId}/send/button-response` - סימולציית לחיצת כפתור
- `POST /api/phones/{phoneId}/send/list-response` - סימולציית בחירה מתפריט
- `GET /api/phones/{phoneId}/send/status` - סטטוס חיבור WhatsApp
- `GET /api/phones/{phoneId}/send/qrcode` - קבלת QR
- `GET /api/phones/{phoneId}/send/qrcode/image` - תמונת QR

### Contacts & Messages
- `GET /api/phones/{phoneId}/contacts` - אנשי קשר
- `GET /api/phones/{phoneId}/contacts/{contactId}/messages` - היסטוריית הודעות

### Internal (אוטומטי)
- `POST /api/webhook/container-event/{phoneId}` - קבלת אירועים מהקונטיינרים
"
    });
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Initialize Container Manager on startup
var containerManager = app.Services.GetRequiredService<IContainerManager>();
await containerManager.InitializeAsync();

// Configure pipeline
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors();
app.UseSerilogRequestLogging();

app.MapControllers();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

Log.Information("WhatsApp Docker Manager starting on {Urls}", builder.Configuration["Urls"] ?? "http://localhost:5000");

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
