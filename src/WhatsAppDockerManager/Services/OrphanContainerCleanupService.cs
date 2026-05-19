using WhatsAppDockerManager.Services;
using WhatsAppDockerManager.Models;
namespace WhatsAppDockerManager.Services;
//journalctl -u whatsapp-manager -f --grep "\[Orphan\]"
//journalctl -u whatsapp-manager --grep "\[Orphan\]" --since "1 hour ago" --no-pager
//NO LIVE
//journalctl -u whatsapp-manager --grep "\[Orphan\]" --no-pager | tail -100 
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
        _dockerService    = dockerService;
        _supabaseService  = supabaseService;
        _containerManager = containerManager;
        _configuration    = configuration;
        _logger           = logger;
    }

    public async Task RunCleanupAsync()
    {
        _logger.LogInformation("🧹 Starting orphan container cleanup...");
        try
        {
            var hostId = _containerManager.CurrentHostId;
            if (hostId == null)
            {
                _logger.LogWarning("[Orphan] Host not initialized — skipping orphan cleanup");
                return;
            }

            // ── כל ה-containers המקומיים (עם label app=whatsapp-manager) ──
            var localContainers = await _dockerService.ListContainersAsync(all: true);

            // ── כל ה-phone_ids הרשומים ב-DB לhost הזה ─────────────────────
            var dbPhones = await _supabaseService.GetPhonesForHostAsync(hostId.Value);

            var dbContainerIds = dbPhones
                .Select(p => p.ContainerId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet();

            // ── גם phone_ids — לזיהוי לפי label ───────────────────────────
            var dbPhoneIds = dbPhones
                .Select(p => p.Id.ToString())
                .ToHashSet();

            _logger.LogInformation(
                "📊 Local containers: {Local} | DB phones: {Db}",
                localContainers.Count, dbPhones.Count);

            var basePath = _configuration["AppSettings:Docker:DataBasePath"] ?? "/opt/whatsapp-data";
            int removed = 0;

            foreach (var container in localContainers)
            {
                var name = container.Names.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12];

                // ── container רשום ב-DB לפי container_id → תקין ──────────
                if (dbContainerIds.Contains(container.ID))
                {
                    _logger.LogDebug("[Orphan]✅ OK: {Name} ({Id})", name, container.ID[..12]);
                    continue;
                }

                // ── container יש לו phone_id label שרשום ב-DB → תקין ─────
                if (container.Labels.TryGetValue("phone_id", out var labelPhoneId)
                    && dbPhoneIds.Contains(labelPhoneId))
                {
                    _logger.LogDebug("[Orphan]✅ OK (by phone_id label): {Name}", name);
                    continue;
                }

                // ── orphan — מחק ──────────────────────────────────────────
                _logger.LogWarning("[Orphan]🗑️ Orphan: {Name} ({Id}) phone_id={PhoneId} — removing",
                    name, container.ID[..12], labelPhoneId ?? "none");

                await _dockerService.RemoveContainerAsync(container.ID);
                removed++;

                // ── מחק תיקיות לפי phone_id (פורמט חדש) ─────────────────
                if (!string.IsNullOrEmpty(labelPhoneId))
                {
                    foreach (var folder in new[] { $"auth_{labelPhoneId}", $"logs_{labelPhoneId}", $"contacts_{labelPhoneId}" })
                    {
                        var path = Path.Combine(basePath, folder);
                        if (Directory.Exists(path))
                        {
                            Directory.Delete(path, recursive: true);
                            _logger.LogInformation("🗑️ Deleted folder: {Path}", path);
                        }
                    }
                }
                // ── fallback: תיקיות ישנות לפי phone_number (פורמט ישן) ──
                else if (container.Labels.TryGetValue("phone_number", out var phoneNumber))
                {
                    var phoneIndex = phoneNumber.Replace("+", "");
                    foreach (var folder in new[] { $"auth_{phoneIndex}", $"logs_{phoneIndex}", $"contacts_{phoneIndex}" })
                    {
                        var path = Path.Combine(basePath, folder);
                        if (Directory.Exists(path))
                        {
                            Directory.Delete(path, recursive: true);
                            _logger.LogInformation("[Orphan]🗑️ Deleted legacy folder: {Path}", path);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("[Orphan]⚠️ No phone_id/phone_number label on {Name} — folders not cleaned", name);
                }
            }

            _logger.LogInformation("[Orphan]✅ Orphan cleanup complete. Removed: {Count} containers", removed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Orphan] Orphan cleanup failed");
        }
    }
}
