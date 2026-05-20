using WhatsAppDockerManager.Services;
using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services;
/// <summary>
/// journalctl -u whatsapp-manager.service -f --no-pager | grep -E "ORPHAN|Duplicate|Error|error|Exception"
/// </summary>
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
                "[ORPHAN] Local containers: {Local} | DB registered: {Db}",
                localContainers.Count, dbContainerIds.Count);

            var basePath = _configuration["AppSettings:Docker:DataBasePath"] ?? "/opt/whatsapp-data";

            foreach (var container in localContainers)
            {
                if (dbContainerIds.Contains(container.ID))
                    continue;

                _logger.LogWarning("[ORPHAN] Orphan: {Name} ({Id}) — removing",
                    container.Names.FirstOrDefault(), container.ID);

                await _dockerService.RemoveContainerAsync(container.ID);

                // ← לפי phone_id (GUID) — תואם ל-PhonePathHelper
                if (container.Labels.TryGetValue("phone_id", out var phoneIdStr)
                    && Guid.TryParse(phoneIdStr, out var phoneIdGuid))
                {
                    PhonePathHelper.DeleteDirectories(basePath, phoneIdGuid);
                    _logger.LogInformation("[ORPHAN] Deleted data dirs for phoneId={PhoneId}", phoneIdGuid);
                }
                else if (container.Labels.TryGetValue("phone_number", out var phoneNumber))
                {
                    // fallback לגרסאות ישנות שאין להן label phone_id
                    _logger.LogWarning("[ORPHAN] No phone_id label on container {Id} — cannot delete data dirs", container.ID);
                }
            }

            _logger.LogInformation("[ORPHAN] Orphan cleanup complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ORPHAN] Orphan cleanup failed");
        }
    }
}