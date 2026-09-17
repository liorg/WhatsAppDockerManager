using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services;

/// <summary>
/// דוגם תבניות PENDING מול הקונטיינר ומעדכן את הסטטוס.
/// אותה לוגיקה כמו TemplatesController.FindStatusTemplate, בתזמון.
/// </summary>
public class TemplateStatusSyncService : BackgroundService
{
    private readonly ISupabaseService   _supabase;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TemplateStatusSyncService> _logger;
    private readonly TimeSpan _interval;

    public TemplateStatusSyncService(
        ISupabaseService supabase,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<TemplateStatusSyncService> logger)
    {
        _supabase          = supabase;
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
        _interval          = TimeSpan.FromSeconds(
            config.GetValue<int?>("AppSettings:Templates:SyncIntervalSeconds") ?? 120);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var n = await SyncOnceAsync(stoppingToken);
                if (n > 0) _logger.LogInformation("[TEMPLATE-SYNC] Updated {Count} template(s)", n);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TEMPLATE-SYNC] Sync pass failed");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var pending = await _supabase.GetPendingTemplatesAsync();
        if (pending.Count == 0) return 0;

        var phones  = new Dictionary<Guid, Phone?>();
        var updated = 0;

        foreach (var tpl in pending)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(tpl.ProviderTemplateId)) continue;

            if (!phones.TryGetValue(tpl.PhoneId, out var phone))
            {
                phone = await _supabase.GetPhoneByIdAsync(tpl.PhoneId);
                phones[tpl.PhoneId] = phone;
            }
            if (string.IsNullOrEmpty(phone?.DockerUrl)) continue;

            var raw = await GetFromContainer(
                phone.DockerUrl, $"/templates/{Uri.EscapeDataString(tpl.ProviderTemplateId)}", ct);
            if (raw == null) continue;

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var rawStatus = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (rawStatus == null) continue;

            var status = MapStatus(rawStatus);
            if (status == tpl.Status) continue;

            var reason    = doc.RootElement.TryGetProperty("rejected_reason", out var r) ? r.GetString() : null;
            var oldStatus = tpl.Status;

            tpl.Status         = status;
            tpl.RejectedReason = reason;
            tpl.UpdatedAt      = DateTime.UtcNow;
            // תבנית מאושרת שאינה מפורסמת אינה ניתנת לשליחה.
            if (status == "approved") tpl.IsPublished = true;

            await _supabase.UpdatePhoneTemplateAsync(tpl);

            _logger.LogInformation("[TEMPLATE-SYNC] {Name}/{Lang} {Old} → {New}",
                tpl.Name, tpl.Lang, oldStatus, status);
            updated++;
        }

        return updated;
    }

    private async Task<string?> GetFromContainer(string dockerUrl, string path, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            using var res = await client.GetAsync($"{dockerUrl}{path}", ct);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[TEMPLATE-SYNC] Container unreachable | {Url}{Path}", dockerUrl, path);
            return null;
        }
    }

    private static string MapStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "APPROVED" => "approved",
        "REJECTED" => "rejected",
        "PAUSED"   => "pause",
        _          => "pending",
    };
}
