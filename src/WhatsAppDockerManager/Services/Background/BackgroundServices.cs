using WhatsAppDockerManager.Configuration;

namespace WhatsAppDockerManager.Services.Background;

/// <summary>
/// Sends heartbeat to DB and checks for dead hosts
/// </summary>
public class HeartbeatService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly HostSettings _hostSettings;

    public HeartbeatService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<HeartbeatService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hostSettings = configuration.GetSection("AppSettings:Host").Get<HostSettings>() ?? new();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for initialization
        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var supabaseService = scope.ServiceProvider.GetRequiredService<ISupabaseService>();
                var containerManager = scope.ServiceProvider.GetRequiredService<IContainerManager>();

                if (containerManager.CurrentHostId.HasValue)
                {
                    // Update our heartbeat
                    await supabaseService.UpdateHostHeartbeatAsync(containerManager.CurrentHostId.Value);
                    _logger.LogDebug("Heartbeat sent");

                    // Check for dead hosts (no heartbeat for 2 minutes)
                    var activeHosts = await supabaseService.GetActiveHostsAsync();
                    var deadThreshold = DateTime.UtcNow.AddMinutes(-2);

                    foreach (var host in activeHosts)
                    {
                        if (host.Id != containerManager.CurrentHostId && 
                            host.LastHeartbeat < deadThreshold)
                        {
                            _logger.LogWarning("Detected dead host: {HostName} (last heartbeat: {LastHeartbeat})", 
                                host.HostName, host.LastHeartbeat);
                            
                            // Take over its phones
                            await containerManager.TakeOverFromDeadHostAsync(host.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in heartbeat service");
            }

            await Task.Delay(TimeSpan.FromSeconds(_hostSettings.HeartbeatIntervalSeconds), stoppingToken);
        }
    }
}

/// <summary>
/// Performs health checks on all containers
/// </summary>
public class HealthCheckService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HealthCheckService> _logger;
    private readonly HostSettings _hostSettings;

    public HealthCheckService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<HealthCheckService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hostSettings = configuration.GetSection("AppSettings:Host").Get<HostSettings>() ?? new();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for initialization
        await Task.Delay(30000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var containerManager = scope.ServiceProvider.GetRequiredService<IContainerManager>();

                await containerManager.HealthCheckAllAsync();
                _logger.LogDebug("Health check completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in health check service");
            }

            await Task.Delay(TimeSpan.FromSeconds(_hostSettings.HealthCheckIntervalSeconds), stoppingToken);
        }
    }
}

/// <summary>
/// Syncs containers with database periodically
/// </summary>
public class ContainerSyncService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ContainerSyncService> _logger;

    public ContainerSyncService(
        IServiceProvider serviceProvider,
        ILogger<ContainerSyncService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for initialization
        await Task.Delay(10000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var containerManager = scope.ServiceProvider.GetRequiredService<IContainerManager>();

                await containerManager.SyncContainersAsync();
                _logger.LogDebug("Container sync completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in container sync service");
            }

            // Sync every 5 minutes
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
