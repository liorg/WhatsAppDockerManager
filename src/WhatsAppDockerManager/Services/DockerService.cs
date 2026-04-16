using Docker.DotNet;
using Docker.DotNet.Models;
using WhatsAppDockerManager.Configuration;
using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services;

public interface IDockerService
{
    Task<bool> PullImageAsync(string imageName);
    Task<string?> CreateAndStartContainerAsync(Phone phone);
    Task<bool> StopContainerAsync(string containerId);
    Task<bool> RemoveContainerAsync(string containerId);
    Task<ContainerInspectResponse?> InspectContainerAsync(string containerId);
    Task<bool> IsContainerRunningAsync(string containerId);
    Task<IList<ContainerListResponse>> ListContainersAsync(bool all = false);
    Task<bool> CheckHealthAsync(string containerId, int apiPort);
}

public class DockerService : IDockerService, IDisposable
{
    private readonly DockerClient _client;
    private readonly ILogger<DockerService> _logger;
    private readonly DockerSettings _dockerSettings;
    private readonly HostSettings _hostSettings;
    private readonly IConfiguration _configuration;

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
        if (OperatingSystem.IsWindows())
            return "npipe://./pipe/docker_engine";
        return "unix:///var/run/docker.sock";
    }

    public async Task<bool> PullImageAsync(string imageName)
    {
        try
        {
            _logger.LogInformation("Pulling Docker image: {ImageName}", imageName);

            var progress = new Progress<JSONMessage>(message =>
            {
                if (!string.IsNullOrEmpty(message.Status))
                    _logger.LogDebug("Pull progress: {Status} {Progress}", message.Status, message.ProgressMessage);
            });

            var parts = imageName.Split(':');
            var name  = parts[0];
            var tag   = parts.Length > 1 ? parts[1] : "latest";

            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = name, Tag = tag },
                null,
                progress);

            _logger.LogInformation("Successfully pulled image: {ImageName}", imageName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pull image: {ImageName}", imageName);
            return false;
        }
    }

    public async Task<string?> CreateAndStartContainerAsync(Phone phone)
    {
        try
        {
            var containerName = $"whatsapp_{phone.Number.Replace("+", "")}";
            var phoneIndex    = phone.Number.Replace("+", "")
                                    .Substring(Math.Max(0, phone.Number.Replace("+", "").Length - 3));

            // ── חישוב ports לפי hash ─────────────────────────────
            var (fastApiPort, baileysPort) = PortHashCalculator.GetBothPorts(
                phone.Number, _configuration);

            _logger.LogInformation(
                "Phone {Phone} → FastAPI:{FastApi} Baileys:{Baileys}",
                phone.Number, fastApiPort, baileysPort);

            // Data paths
            var basePath     = _dockerSettings.DataBasePath;
            var authPath     = Path.Combine(basePath, $"auth_{phoneIndex}");
            var redisPath    = Path.Combine(basePath, $"redis_{phoneIndex}");
            var logsPath     = Path.Combine(basePath, $"logs_{phoneIndex}");
            var contactsPath = Path.Combine(basePath, $"contacts_{phoneIndex}");

            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(authPath);
                Directory.CreateDirectory(redisPath);
                Directory.CreateDirectory(logsPath);
                Directory.CreateDirectory(contactsPath);
            }

            // בדוק אם קונטיינר קיים ומחק
            var existingContainers = await _client.Containers
                .ListContainersAsync(new ContainersListParameters { All = true });
            var existing = existingContainers.FirstOrDefault(c =>
                c.Names.Any(n => n.TrimStart('/') == containerName));
            if (existing != null)
            {
                _logger.LogWarning("Container {Name} already exists, removing...", containerName);
                await RemoveContainerAsync(existing.ID);
            }

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
                    },
                    // הקונטיינר מאזין תמיד על 8000 (FastAPI) ו-3001 (Baileys)
                    ExposedPorts = new Dictionary<string, EmptyStruct>
                    {
                        { "8000/tcp", default },
                        { "3001/tcp", default }
                    },
                    HostConfig = new HostConfig
                    {
                        PortBindings = new Dictionary<string, IList<PortBinding>>
                        {
                            {
                                "8000/tcp",
                                new List<PortBinding> { new() { HostPort = fastApiPort.ToString() } }
                            },
                            {
                                "3001/tcp",
                                new List<PortBinding> { new() { HostPort = baileysPort.ToString() } }
                            }
                        },
                        Binds = new List<string>
                        {
                            $"{authPath}:/app/auth_info",
                            $"{redisPath}:/var/lib/redis",
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

            var started = await _client.Containers
                .StartContainerAsync(createResponse.ID, new ContainerStartParameters());

            if (!started)
            {
                _logger.LogError("Failed to start container {Name}", containerName);
                return null;
            }

            _logger.LogInformation(
                "Container {Name} started. FastAPI:{FastApi} Baileys:{Baileys}",
                containerName, fastApiPort, baileysPort);

            return createResponse.ID;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating container for phone {Phone}", phone.Number);
            return null;
        }
    }

    public async Task<bool> StopContainerAsync(string containerId)
    {
        try
        {
            await _client.Containers.StopContainerAsync(containerId, new ContainerStopParameters
            {
                WaitBeforeKillSeconds = 10
            });
            _logger.LogInformation("Container {ContainerId} stopped", containerId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping container {ContainerId}", containerId);
            return false;
        }
    }

    public async Task<bool> RemoveContainerAsync(string containerId)
    {
        try
        {
            try { await StopContainerAsync(containerId); }
            catch { }

            await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters
            {
                Force         = true,
                RemoveVolumes = false
            });
            _logger.LogInformation("Container {ContainerId} removed", containerId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing container {ContainerId}", containerId);
            return false;
        }
    }

    public async Task<ContainerInspectResponse?> InspectContainerAsync(string containerId)
    {
        try
        {
            return await _client.Containers.InspectContainerAsync(containerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inspecting container {ContainerId}", containerId);
            return null;
        }
    }

    public async Task<bool> IsContainerRunningAsync(string containerId)
    {
        try
        {
            var inspection = await InspectContainerAsync(containerId);
            return inspection?.State?.Running ?? false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IList<ContainerListResponse>> ListContainersAsync(bool all = false)
    {
        try
        {
            return await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All     = all,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    { "label", new Dictionary<string, bool> { { "app=whatsapp-manager", true } } }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing containers");
            return new List<ContainerListResponse>();
        }
    }

    public async Task<bool> CheckHealthAsync(string containerId, int apiPort)
    {
        try
        {
            if (!await IsContainerRunningAsync(containerId))
                return false;

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync($"http://localhost:{apiPort}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}