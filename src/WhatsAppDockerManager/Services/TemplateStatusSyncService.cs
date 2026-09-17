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
        _logger.LogInformation("[TEMPLATE-SYNC] Started, interval={Seconds}s", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TEMPLATE-SYNC] Sync pass failed");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }

        _logger.LogInformation("[TEMPLATE-SYNC] Stopped");
    }

    private async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var pending = await _supabase.GetPendingTemplatesAsync();

        int noProviderId = 0, noContainer = 0, noAnswer = 0, unchanged = 0, updated = 0;

        foreach (var tpl in pending)
        {
            if (ct.IsCancellationRequested) break;

            if (string.IsNullOrEmpty(tpl.ProviderTemplateId)) { noProviderId++; continue; }

            if (!phones.TryGetValue(tpl.PhoneId, out var phone))
            {
                phone = await _supabase.GetPhoneByIdAsync(tpl.PhoneId);
                phones[tpl.PhoneId] = phone;
            }
            if (string.IsNullOrEmpty(phone?.DockerUrl)) { noContainer++; continue; }

            var raw = await GetFromContainer(
                phone.DockerUrl, $"/templates/{Uri.EscapeDataString(tpl.ProviderTemplateId)}", ct);
            if (raw == null) { noAnswer++; continue; }

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var rawStatus = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (rawStatus == null) { noAnswer++; continue; }

            var status = MapStatus(rawStatus);
            if (status == tpl.Status) { unchanged++; continue; }

            // ... בלוק העדכון כמו שהוא ...
            updated++;
        }

        _logger.LogInformation(
            "[TEMPLATE-SYNC] pending={Pending} updated={Updated} unchanged={Unchanged} " +
            "noProviderId={NoId} noContainer={NoContainer} noAnswer={NoAnswer}",
            pending.Count, updated, unchanged, noProviderId, noContainer, noAnswer);

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
