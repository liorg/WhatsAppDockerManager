using Docker.DotNet;
using Docker.DotNet.Models;
using WhatsAppDockerManager.Configuration;
using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services;

public interface IDockerService
{
    Task<bool> PullImageAsync(string imageName);
   // Task<string?> CreateAndStartContainerAsync(Phone phone, int fastApiPort, int baileysPort, int authRevision = 0);
    Task<string?> CreateAndStartContainerAsync(Phone phone, int fastApiPort, int baileysPort, int authRevision = 0, string maskedUsername = "****user");
    Task<bool> StopContainerAsync(string containerId);
    Task<bool> RemoveContainerAsync(string containerId);
    Task<ContainerInspectResponse?> InspectContainerAsync(string containerId);
    Task<bool> IsContainerRunningAsync(string containerId);
    Task<IList<ContainerListResponse>> ListContainersAsync(bool all = false);
    Task<bool> CheckHealthAsync(string containerId, int apiPort);
    Task EnsureNetworkExistsAsync(string networkName);
    Task EnsureRedisContainerRunningAsync();
    Task<DockerImageInfo?> GetImageInfoAsync(string imageName);   // ← חדש
     Task RemoveContainerByNameAsync(string containerName);
}

// ── Model ─────────────────────────────────────────────────────────────
public class DockerImageInfo
{
    public string?       Id       { get; set; }
    public DateTime      Created  { get; set; }
    public List<string>? RepoTags { get; set; }
}

public class DockerService : IDockerService, IDisposable
{
    private readonly DockerClient _client;
    private readonly ILogger<DockerService> _logger;
    private readonly DockerSettings _dockerSettings;
    private readonly HostSettings _hostSettings;
    private readonly IConfiguration _configuration;

    private const string RedisContainerName = "redis_shared";
    private const string NetworkName        = "whatsapp_network";

    public DockerService(IConfiguration configuration, ILogger<DockerService> logger)
    {
        _logger         = logger;
        _configuration  = configuration;
        _dockerSettings = configuration.GetSection("AppSettings:Docker").Get<DockerSettings>() ?? new();
        _hostSettings   = configuration.GetSection("AppSettings:Host").Get<HostSettings>() ?? new();

        var dockerUri = GetDockerUri();
        _logger.LogInformation("Connecting to Docker at {Uri}", dockerUri);
        _client = new DockerClientConfiguration(new Uri(dockerUri)).CreateClient();
    }

    private static string GetDockerUri()
    {
        if (OperatingSystem.IsWindows()) return "npipe://./pipe/docker_engine";
        return "unix:///var/run/docker.sock";
    }

    public async Task<bool> PullImageAsync(string imageName)
    {
        try
        {
            _logger.LogInformation("[DOCKER] Pulling Docker image: {ImageName}", imageName);
            var progress = new Progress<JSONMessage>(message =>
            {
                if (!string.IsNullOrEmpty(message.Status))
                    _logger.LogDebug("[DOCKER] Pull progress: {Status} {Progress}", message.Status, message.ProgressMessage);
            });
            var parts = imageName.Split(':');
            var name  = parts[0];
            var tag   = parts.Length > 1 ? parts[1] : "latest";
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = name, Tag = tag }, null, progress);
            _logger.LogInformation("[DOCKER] Successfully pulled image: {ImageName}", imageName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DOCKER] Failed to pull image: {ImageName}", imageName);
            return false;
        }
    }

    // ── GetImageInfoAsync ─────────────────────────────────────────────
    public async Task<DockerImageInfo?> GetImageInfoAsync(string imageName)
    {
        try
        {
            var images = await _client.Images.ListImagesAsync(
                new ImagesListParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["reference"] = new Dictionary<string, bool> { [imageName] = true }
                    }
                });

            var img = images.FirstOrDefault();
            if (img == null) return null;

            return new DockerImageInfo
            {
                Id       = img.ID,
                Created  = img.Created,
                RepoTags = img.RepoTags?.ToList(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get image info for {Image}", imageName);
            return null;
        }
    }

    public async Task RemoveContainerByNameAsync(string containerName)
    {
        try
        {
            var containers = await _client.Containers
                .ListContainersAsync(new ContainersListParameters { All = true });
            var existing = containers.FirstOrDefault(c =>
                c.Names.Any(n => n.TrimStart('/') == containerName));
            if (existing == null)
            {
                _logger.LogInformation("[DOCKER] No container found with name {Name} — nothing to remove", containerName);
                return;
            }
            await RemoveContainerAsync(existing.ID);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DOCKER] RemoveContainerByNameAsync failed for {Name}", containerName);
        }
    }

    public async Task<string?> CreateAndStartContainerAsync(Phone phone, int fastApiPort, int baileysPort, int authRevision = 0, string maskedUsername = "****user")
    {
        try
        {
            _logger.LogInformation("[DOCKER] Creating container | phone={Phone} fastApi={FastApi} baileys={Baileys}",
                phone.Number, fastApiPort, baileysPort);

            var containerName = PhonePathHelper.ContainerName(phone.Number, phone.Id);
            var basePath      = _dockerSettings.DataBasePath;
            var authPath      = PhonePathHelper.AuthPath(basePath, phone.Id);
            var logsPath      = PhonePathHelper.LogsPath(basePath, phone.Id);
            var contactsPath  = PhonePathHelper.ContactsPath(basePath, phone.Id);

            _logger.LogInformation("[DOCKER] Paths: auth={Auth} logs={Logs} contacts={Contacts}",
                authPath, logsPath, contactsPath);

            PhonePathHelper.EnsureDirectoriesExist(basePath, phone.Id);

            // הסר container קיים עם אותו שם
            var existingContainers = await _client.Containers
                .ListContainersAsync(new ContainersListParameters { All = true });
            var existing = existingContainers.FirstOrDefault(c =>
                c.Names.Any(n => n.TrimStart('/') == containerName));
            if (existing != null)
            {
                _logger.LogWarning("[DOCKER] Container {Name} already exists, removing...", containerName);
                await RemoveContainerAsync(existing.ID);
            }
            var ManagerUrl="http://172.17.0.1:5000";

            var createResponse = await _client.Containers
                .CreateContainerAsync(new CreateContainerParameters
                {
                    Image = _dockerSettings.ImageName,
                    Name  = containerName,
                    Env   = new List<string>
                    {
                        $"TZ={_dockerSettings.Timezone}",
                        $"PHONE_NUMBER={phone.Number}",
                        $"PHONE_ID={phone.Id}",
                        $"REDIS_URL=redis://{RedisContainerName}:6379",
                        $"AUTH_REVISION={authRevision}",
                        $"USER_DISPLAY={maskedUsername}",  
                        $"MANAGER_URL={ManagerUrl}",   
                    },
                    ExposedPorts = new Dictionary<string, EmptyStruct>
                    {
                        { "8000/tcp", default },
                        { "3001/tcp", default }
                    },
                    HostConfig = new HostConfig
                    {
                        PortBindings = new Dictionary<string, IList<PortBinding>>
                        {
                            { "8000/tcp", new List<PortBinding> { new() { HostPort = fastApiPort.ToString() } } },
                            { "3001/tcp", new List<PortBinding> { new() { HostPort = baileysPort.ToString() } } }
                        },
                        Binds = new List<string>
                        {
                            $"{authPath}:/app/auth_info",
                            $"{logsPath}:/var/log",
                            $"{contactsPath}:/app/data"
                        },
                        RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                        Memory    = 512 * 1024 * 1024,
                        CPUShares = 512
                    },
                    Labels = new Dictionary<string, string>
                    {
                        { "app",          "whatsapp-manager" },
                        { "phone_id",     phone.Id.ToString() },
                        { "phone_number", phone.Number },
                        { "fastapi_port", fastApiPort.ToString() },
                        { "baileys_port", baileysPort.ToString() }
                    }
                });

            _logger.LogInformation("[DOCKER] Container {Name} created with ID {Id}", containerName, createResponse.ID);

            var started = await _client.Containers
                .StartContainerAsync(createResponse.ID, new ContainerStartParameters());

            if (!started)
            {
                _logger.LogError("[DOCKER] Failed to start container {Name}", containerName);
                return null;
            }

            await _client.Networks.ConnectNetworkAsync(NetworkName,
                new NetworkConnectParameters { Container = createResponse.ID });

            _logger.LogInformation("[DOCKER] ✓ Container {Name} started. FastAPI:{FastApi} Baileys:{Baileys}",
                containerName, fastApiPort, baileysPort);
            return createResponse.ID;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DOCKER] Error creating container for phone {Phone}", phone.Number);
            return null;
        }
    }

    public async Task<bool> StopContainerAsync(string containerId)
    {
        try
        {
            await _client.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
            _logger.LogInformation("Container {ContainerId} stopped", containerId);
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Error stopping container {ContainerId}", containerId); return false; }
    }

    public async Task<bool> RemoveContainerAsync(string containerId)
    {
        try
        {
            try { await StopContainerAsync(containerId); } catch { }
            await _client.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = false });
            _logger.LogInformation("[DOCKER] Container {ContainerId} removed", containerId);
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Error removing container {ContainerId}", containerId); return false; }
    }

    public async Task<ContainerInspectResponse?> InspectContainerAsync(string containerId)
    {
        try { return await _client.Containers.InspectContainerAsync(containerId); }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Error inspecting container {ContainerId}", containerId); return null; }
    }

    public async Task<bool> IsContainerRunningAsync(string containerId)
    {
        try { var i = await InspectContainerAsync(containerId); return i?.State?.Running ?? false; }
        catch { return false; }
    }

    public async Task<IList<ContainerListResponse>> ListContainersAsync(bool all = false)
    {
        try
        {
            _logger.LogInformation("[DOCKER] Listing containers (all={All})", all); 
            return await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All     = all,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    { "label", new Dictionary<string, bool> { { "app=whatsapp-manager", true } } }
                }
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Error listing containers"); return new List<ContainerListResponse>(); }
    }

    public async Task<bool> CheckHealthAsync(string containerId, int apiPort)
    {
        try
        {
            _logger.LogInformation("[DOCKER]  Checking health of container {ContainerId} on port {Port}", containerId, apiPort);  
            if (!await IsContainerRunningAsync(containerId)) return false;
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync($"http://localhost:{apiPort}/health");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task EnsureNetworkExistsAsync(string networkName)
    {
        try
        {
            var networks = await _client.Networks.ListNetworksAsync(new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    { "name", new Dictionary<string, bool> { { networkName, true } } }
                }
            });
            if (networks.Any(n => n.Name == networkName))
            {
                _logger.LogInformation("Network {Network} already exists", networkName);
                return;
            }
            await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters { Name = networkName, Driver = "bridge" });
            _logger.LogInformation("Created Docker network: {Network}", networkName);
        }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Failed to ensure network {Network} exists", networkName); throw; }
    }

    public async Task EnsureRedisContainerRunningAsync()
    {
        const string containerName = "redis_shared";
        const string imageName     = "redis:7-alpine";
        const string networkName   = "whatsapp_network";
        try
        {
            var containers = await _client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true });
            var existing = containers.FirstOrDefault(c =>
                c.Names.Any(n => n.TrimStart('/') == containerName));

            if (existing != null)
            {
                if (existing.State == "running") { _logger.LogInformation("[DOCKER] Redis container already running"); return; }
                await _client.Containers.StartContainerAsync(existing.ID, new ContainerStartParameters());
                _logger.LogInformation("[DOCKER] Started existing Redis container");
                return;
            }

            await PullImageAsync(imageName);

            var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image      = imageName,
                Name       = containerName,
                HostConfig = new HostConfig
                {
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                    Memory = 256 * 1024 * 1024,
                }
            });
            await _client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
            await _client.Networks.ConnectNetworkAsync(networkName,
                new NetworkConnectParameters { Container = response.ID });
            _logger.LogInformation("[DOCKER] Redis container created and started");
        }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Failed to ensure Redis container"); throw; }
    }

    public void Dispose() { _client?.Dispose(); }
}
