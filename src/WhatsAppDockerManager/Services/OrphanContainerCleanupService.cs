using WhatsAppDockerManager.Services;
using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services;

public class OrphanContainerCleanupService
{
    private readonly IDockerService _dockerService;
    private readonly ISupabaseService _supabaseService;
    private readonly IContainerManager _containerManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OrphanContainerCleanupService> _logger;

    public OrphanContainerCleanupService(
        IDockerService dockerService,
        ISupabaseService supabaseService,
        IContainerManager containerManager,
        IConfiguration configuration,
        ILogger<OrphanContainerCleanupService> logger)
    {
        _dockerService = dockerService;
        _supabaseService = supabaseService;
        _containerManager = containerManager;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task RunCleanupAsync()
    {
        _logger.LogInformation("Starting orphan container cleanup...");

        try
        {
            var hostId = _containerManager.CurrentHostId;
            if (hostId == null)
            {
                _logger.LogWarning("Host not initialized — skipping orphan cleanup");
                return;
            }

            var localContainers = await _dockerService.ListContainersAsync(all: true);

            var dbPhones = await _supabaseService.GetPhonesForHostAsync(hostId.Value);
            var dbContainerIds = dbPhones
                .Select(p => p.ContainerId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet();

            _logger.LogInformation(
                "Local containers: {Local} | DB registered: {Db}",
                localContainers.Count, dbContainerIds.Count);

            var basePath = _configuration["AppSettings:Docker:DataBasePath"] ?? "/opt/whatsapp-data";

            foreach (var container in localContainers)
            {
                if (dbContainerIds.Contains(container.ID))
                    continue;

                _logger.LogWarning("Orphan: {Name} ({Id}) — removing",
                    container.Names.FirstOrDefault(), container.ID);

                await _dockerService.RemoveContainerAsync(container.ID);

                if (container.Labels.TryGetValue("phone_number", out var phoneNumber))
                {
                    var phoneIndex = phoneNumber.Replace("+", "");
                    foreach (var folder in new[] { $"auth_{phoneIndex}", $"logs_{phoneIndex}", $"contacts_{phoneIndex}" })
                    {
                        var path = Path.Combine(basePath, folder);
                        if (Directory.Exists(path))
                        {
                            Directory.Delete(path, recursive: true);
                            _logger.LogInformation("Deleted: {Path}", path);
                        }
                    }
                }
            }

            _logger.LogInformation("Orphan cleanup complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orphan cleanup failed");
        }
    }
} 