using Serilog;
using WhatsAppDockerManager.Configuration;
using WhatsAppDockerManager.Services;
using System.Reflection;
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

builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));

builder.Services.AddSingleton<ISupabaseService, SupabaseService>();
builder.Services.AddSingleton<IDockerService, DockerService>();
builder.Services.AddSingleton<IContainerManager, ContainerManager>();
builder.Services.AddSingleton<OrphanContainerCleanupService>();

builder.Services.AddHttpClient();

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
        Title   = "WhatsApp Docker Manager API",
        Version = "v2.0",
        Description = @"
## WhatsApp Docker Manager API

### Phones
- `GET /api/phones` - רשימת כל הטלפונים
- `GET /api/phones/{phoneId}` - פרטי טלפון

### Send Messages (מעטפת - Docker לא חשוף)
- `POST /api/phones/{phoneId}/send/text` - שליחת טקסט

### Contacts & Messages
- `GET /api/phones/{phoneId}/contacts` - אנשי קשר

### Internal (אוטומטי)
- `POST /api/webhook/container-event/{phoneId}` - קבלת אירועים מהקונטיינרים
"
    });
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();

// ── Initialize ────────────────────────────────────────────────────
IContainerManager? containerManager = null;
try
{
    containerManager = app.Services.GetRequiredService<IContainerManager>();
    await containerManager.InitializeAsync();   // ← pull קורה כאן

    var orphanCleanup = app.Services.GetRequiredService<OrphanContainerCleanupService>();
    await orphanCleanup.RunCleanupAsync();
}
catch (Exception ex)
{
    Log.Error(ex, "Startup initialization failed — check SUPABASE_URL, SUPABASE_KEY, and Docker socket");
}

app.UseSerilogRequestLogging();
app.MapControllers();

// ── Health ────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// ── Version + Image info ──────────────────────────────────────────
app.MapGet("/version", async (IContainerManager cm, IDockerService dockerService, IConfiguration config) =>
{
    var assembly   = Assembly.GetExecutingAssembly();
    var rawVersion = assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
    var cleanVersion = rawVersion.Split('+')[0];

    // ── שלוף image info מ-Docker ──────────────────────────────────
    var imageName   = config["AppSettings:Docker:ImageName"] ?? "whatsapp-single";
    string? imageId      = null;
    string? imageShortId = null;
    DateTime? imageCreated = null;
    string? imageTag     = null;

    try
    {
        var imageInfo = await dockerService.GetImageInfoAsync(imageName);
        if (imageInfo != null)
        {
            imageId      = imageInfo.Id;
            imageShortId = imageInfo.Id?.Length > 12 ? imageInfo.Id[..12] : imageInfo.Id;
            imageCreated = imageInfo.Created;
            imageTag     = imageInfo.RepoTags?.FirstOrDefault() ?? imageName;
        }
    }
    catch { /* non-critical */ }

    return Results.Ok(new
    {
        app         = "WhatsAppDockerManager",
        version     = cleanVersion,
        fullVersion = rawVersion,
        hostId      = cm.CurrentHostId,
        image = new
        {
            name         = imageName,
            tag          = imageTag,
            shortId      = imageShortId,
            id           = imageId,
            created      = imageCreated,
            // digest שנשמר ב-memory אחרי pull בעלייה
            pulledDigest = cm.CurrentImageDigest?[..Math.Min(20, cm.CurrentImageDigest?.Length ?? 0)],
        }
    });
});

Log.Information("WhatsApp Docker Manager starting on {Urls}",
    builder.Configuration["Urls"] ?? "http://localhost:5000");

try   { await app.RunAsync(); }
catch (Exception ex) { Log.Fatal(ex, "Application terminated unexpectedly"); }
finally { Log.CloseAndFlush(); }
