using Serilog;
using WhatsAppDockerManager.Configuration;
using WhatsAppDockerManager.Services;
using WhatsAppDockerManager.Services.Background;
using WhatsAppDockerManager.Services.Proxy;
using Yarp.ReverseProxy.Configuration;
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

// Proxy services
builder.Services.AddSingleton<DynamicProxyConfigProvider>();
builder.Services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<DynamicProxyConfigProvider>());

// Background services
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<HealthCheckService>();
builder.Services.AddHostedService<ContainerSyncService>();
builder.Services.AddHostedService<ProxyRouteSyncService>();
builder.Services.AddHostedService<TcpProxyService>();

// YARP Reverse Proxy
builder.Services.AddReverseProxy();

// Controllers
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "WhatsApp Docker Manager API", Version = "v1" });
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
//if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseSerilogRequestLogging();

app.MapControllers();

// YARP reverse proxy - this handles /api/phone/{number}/** and /api/id/{id}/** routes
app.MapReverseProxy();

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
