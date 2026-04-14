using WhatsAppDockerManager.Configuration;
using WhatsAppDockerManager.Models;
using DbHost = WhatsAppDockerManager.Models.Host;
using Supabase;
namespace WhatsAppDockerManager.Services;

/// <summary>
/// Manages Docker containers for phones, including starting/stopping containers, syncing with database
/// </summary>
public interface IContainerManager
{
    Task InitializeAsync();
    Task<bool> StartPhoneContainerAsync(Phone phone);
    Task<bool> StopPhoneContainerAsync(Phone phone);
    Task<bool> RestartPhoneContainerAsync(Phone phone);
    Task SyncContainersAsync();
    Task HealthCheckAllAsync();
    Task TakeOverFromDeadHostAsync(Guid deadHostId);
    Guid? CurrentHostId { get; }
}

public class ContainerManager : IContainerManager
{
    private readonly IDockerService _dockerService;
    private readonly ISupabaseService _supabaseService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ContainerManager> _logger;
    private readonly HostSettings _hostSettings;
    private readonly DockerSettings _dockerSettings;
    
    private DbHost? _currentHost;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public Guid? CurrentHostId => _currentHost?.Id;

    public ContainerManager(
        IDockerService dockerService,
        ISupabaseService supabaseService,
        IConfiguration configuration,
        ILogger<ContainerManager> logger)
    {
        _dockerService = dockerService;
        _supabaseService = supabaseService;
        _configuration = configuration;
        _logger = logger;
        _hostSettings = configuration.GetSection("AppSettings:Host").Get<HostSettings>() ?? new();
        _dockerSettings = configuration.GetSection("AppSettings:Docker").Get<DockerSettings>() ?? new();
    }

    public async Task InitializeAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            _logger.LogInformation("Initializing Container Manager...");

            // Register/update this host in DB
            _currentHost = await _supabaseService.GetOrCreateHostAsync(
                _hostSettings.HostName,
                _hostSettings.IpAddress,
                _hostSettings.ExternalIp,
                _hostSettings.PortRangeStart,
                _hostSettings.PortRangeEnd,
                _hostSettings.MaxContainers
            );

            if (_currentHost == null)
            {
                throw new InvalidOperationException("Failed to register host in database");
            }

            _logger.LogInformation("Host registered: {HostId} ({HostName})", _currentHost.Id, _currentHost.HostName);

            // Pull the Docker image
            _logger.LogInformation("Ensuring Docker image is available: {Image}", _dockerSettings.ImageName);
            await _dockerService.PullImageAsync(_dockerSettings.ImageName);

            // Sync containers with database
            await SyncContainersAsync();

            _initialized = true;
            _logger.LogInformation("Container Manager initialized successfully");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> StartPhoneContainerAsync(Phone phone)
    {
        if (_currentHost == null)
        {
            _logger.LogError("Host not initialized");
            return false;
        }

        try
        {
            _logger.LogInformation("Starting container for phone {PhoneNumber}", phone.Number);

            // Update status to starting
            await _supabaseService.UpdatePhoneDockerStatusAsync(phone.Id, PhoneDockerStatus.Starting);

            // Calculate port using same method as DockerService
            var apiPort = PortHashCalculator.GetFastApiPort(phone.Number, _configuration);

            // Create and start container (uses same PortHashCalculator internally)
            var containerId = await _dockerService.CreateAndStartContainerAsync(phone);

            if (containerId == null)
            {
                await _supabaseService.UpdatePhoneDockerStatusAsync(
                    phone.Id, 
                    PhoneDockerStatus.Error,
                    errorMessage: "Failed to create container"
                );
                await _supabaseService.LogAgentEventAsync(
                    _currentHost.Id, 
                    AgentEventType.Error,
                    new { phoneId = phone.Id, error = "Failed to create container" }
                );
                return false;
            }

            // Build Docker URL (only FastAPI port exposed)
            var host = !string.IsNullOrEmpty(_hostSettings.ExternalIp) ? _hostSettings.ExternalIp 
                     : !string.IsNullOrEmpty(_hostSettings.IpAddress) ? _hostSettings.IpAddress 
                     : "localhost";
            var dockerUrl = $"http://{host}:{apiPort}";

            // Update phone record
            await _supabaseService.UpdatePhoneDockerStatusAsync(
                phone.Id,
                PhoneDockerStatus.Running,
                containerId: containerId,
                containerName: $"whatsapp_{phone.Number.Replace("+", "")}",
                apiPort: apiPort,
                dockerUrl: dockerUrl
            );

            // Register webhook in container to receive events back
            await RegisterWebhookInContainerAsync(apiPort, phone.Id);

            await _supabaseService.LogAgentEventAsync(
                _currentHost.Id,
                AgentEventType.Started,
                new { phoneId = phone.Id, containerId, apiPort, dockerUrl }
            );
            
            _logger.LogInformation("Container started for phone {PhoneNumber} on port {Port}", phone.Number, apiPort);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting container for phone {PhoneNumber}", phone.Number);
            
            await _supabaseService.UpdatePhoneDockerStatusAsync(
                phone.Id,
                PhoneDockerStatus.Error,
                errorMessage: ex.Message
            );
            
            return false;
        }
    }

    /// <summary>
    /// Register webhook in the container so it sends events back to this manager
    /// </summary>
    private async Task RegisterWebhookInContainerAsync(int apiPort, Guid phoneId)
    {
        try
        {
            // Wait for container to be ready
            await Task.Delay(3000);

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            
            // Docker bridge IP for Linux containers to reach host
            var host = "172.17.0.1";
            var managerWebhook = $"http://{host}:5000/api/webhook/container-event/{phoneId}";

            var payload = new
            {
                url = managerWebhook,
                secret = "manager-secret"
            };

            var response = await httpClient.PostAsJsonAsync(
                $"http://localhost:{apiPort}/webhooks/register",
                payload
            );

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Webhook registered in container for phone {PhoneId}", phoneId);
            }
            else
            {
                _logger.LogWarning("Failed to register webhook in container: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not register webhook in container (container might not be ready yet)");
        }
    }

    public async Task<bool> StopPhoneContainerAsync(Phone phone)
    {
        if (string.IsNullOrEmpty(phone.ContainerId))
        {
            _logger.LogWarning("Phone {PhoneNumber} has no container ID", phone.Number);
            return false;
        }

        try
        {
            var success = await _dockerService.StopContainerAsync(phone.ContainerId);
            
            if (success)
            {
                await _supabaseService.UpdatePhoneDockerStatusAsync(phone.Id, PhoneDockerStatus.Stopped);
                await _supabaseService.LogAgentEventAsync(
                    _currentHost?.Id,
                    AgentEventType.Stopped,
                    new { phoneId = phone.Id }
                );
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping container for phone {PhoneNumber}", phone.Number);
            return false;
        }
    }

    public async Task<bool> RestartPhoneContainerAsync(Phone phone)
    {
        await StopPhoneContainerAsync(phone);
        
        if (!string.IsNullOrEmpty(phone.ContainerId))
        {
            await _dockerService.RemoveContainerAsync(phone.ContainerId);
        }

        return await StartPhoneContainerAsync(phone);
    }

    public async Task SyncContainersAsync()
    {
        if (_currentHost == null) return;

        await _syncLock.WaitAsync();
        try
        {
            _logger.LogInformation("Syncing containers with database...");

            // Get phones assigned to this host
            var phones = await _supabaseService.GetPhonesForHostAsync(_currentHost.Id);
            
            // Get running containers
            var runningContainers = await _dockerService.ListContainersAsync(all: true);
            var runningContainerIds = runningContainers
                .Where(c => c.State == "running")
                .Select(c => c.ID)
                .ToHashSet();

            foreach (var phone in phones)
            {
                // If phone should be running but container isn't
                if (phone.DockerStatus == PhoneDockerStatus.Running && 
                    !string.IsNullOrEmpty(phone.ContainerId) &&
                    !runningContainerIds.Contains(phone.ContainerId))
                {
                    _logger.LogWarning("Container for phone {PhoneNumber} is not running, restarting...", phone.Number);
                    await RestartPhoneContainerAsync(phone);
                }
                // If phone is pending, start it
                else if (phone.DockerStatus == PhoneDockerStatus.Pending || 
                         phone.DockerStatus == PhoneDockerStatus.Unknown)
                {
                    _logger.LogInformation("Starting pending phone {PhoneNumber}", phone.Number);
                    await StartPhoneContainerAsync(phone);
                }
            }

            // Check for orphaned phones (no host assigned) and claim them if we have capacity
            var orphanedPhones = await _supabaseService.GetOrphanedPhonesAsync();
            var currentCount = phones.Count;
            
            foreach (var phone in orphanedPhones)
            {
                if (currentCount >= _hostSettings.MaxContainers)
                {
                    _logger.LogWarning("Host at capacity ({Max}), cannot claim more phones", _hostSettings.MaxContainers);
                    break;
                }

                _logger.LogInformation("Claiming orphaned phone {PhoneNumber}", phone.Number);
                await _supabaseService.AssignPhoneToHostAsync(phone.Id, _currentHost.Id);
                await StartPhoneContainerAsync(phone);
                currentCount++;
            }

            _logger.LogInformation("Container sync completed. Managing {Count} phones", currentCount);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task HealthCheckAllAsync()
    {
        if (_currentHost == null) return;

        try
        {
            var phones = await _supabaseService.GetPhonesForHostAsync(_currentHost.Id);

            foreach (var phone in phones.Where(p => p.DockerStatus == PhoneDockerStatus.Running))
            {
                if (string.IsNullOrEmpty(phone.ContainerId) || !phone.ApiPort.HasValue)
                    continue;

                var isHealthy = await _dockerService.CheckHealthAsync(phone.ContainerId, phone.ApiPort.Value);

                if (!isHealthy)
                {
                    _logger.LogWarning("Phone {PhoneNumber} failed health check", phone.Number);
                    
                    await _supabaseService.LogAgentEventAsync(
                        _currentHost.Id,
                        AgentEventType.HealthCheckFailed,
                        new { phoneId = phone.Id }
                    );

                    // Try to restart
                    await RestartPhoneContainerAsync(phone);
                }
                else
                {
                    // Update last health check time
                    await _supabaseService.UpdatePhoneDockerStatusAsync(
                        phone.Id,
                        PhoneDockerStatus.Running
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during health check");
        }
    }

    public async Task TakeOverFromDeadHostAsync(Guid deadHostId)
    {
        if (_currentHost == null) return;

        try
        {
            _logger.LogWarning("Taking over phones from dead host {DeadHostId}", deadHostId);

            // Get phones from the dead host
            var phones = await _supabaseService.GetPhonesForHostAsync(deadHostId);
            var currentCount = (await _supabaseService.GetPhonesForHostAsync(_currentHost.Id)).Count;

            foreach (var phone in phones)
            {
                if (currentCount >= _hostSettings.MaxContainers)
                {
                    _logger.LogWarning("Host at capacity, cannot take over more phones");
                    break;
                }

                _logger.LogInformation("Taking over phone {PhoneNumber} from dead host", phone.Number);
                
                // Assign to this host
                await _supabaseService.AssignPhoneToHostAsync(phone.Id, _currentHost.Id);
                
                // Start container
                await StartPhoneContainerAsync(phone);
                
                await _supabaseService.LogAgentEventAsync(
                    _currentHost.Id,
                    AgentEventType.Migrated,
                    new { phoneId = phone.Id, fromHostId = deadHostId, toHostId = _currentHost.Id }
                );

                currentCount++;
            }

            // Mark dead host as inactive
            await _supabaseService.SetHostStatusAsync(deadHostId, "inactive");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error taking over from dead host {DeadHostId}", deadHostId);
        }
    }
}
