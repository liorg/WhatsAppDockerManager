using System.Text.Json;
using WhatsAppDockerManager.Models;
using static WhatsAppDockerManager.Services.TemplateMapper;

namespace WhatsAppDockerManager.Services;

/// <summary>
/// שני מעברים מתוזמנים מול הקונטיינר:
///   Sync   — תבניות pending שיש להן provider id → עדכון סטטוס.
///   Import — תבניות שקיימות אצל הספק ואין להן רשומה → הקמה.
///            רשומה קיימת בלי provider id מקבלת השלמה (backfill).
/// הזיהוי בייבוא הוא לוגי — name+lang, כמו ב-WhatsApp — ולא לפי ה-Guid הפנימי.
/// </summary>
public class TemplateStatusSyncService : BackgroundService
{
    private readonly ISupabaseService   _supabase;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TemplateStatusSyncService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _importInterval;

    private DateTime _lastImport = DateTime.MinValue;

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
        _importInterval    = TimeSpan.FromSeconds(
            config.GetValue<int?>("AppSettings:Templates:ImportIntervalSeconds") ?? 900);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[TEMPLATE-SYNC] Started, sync={Sync}s import={Import}s",
            _interval.TotalSeconds, _importInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);

                if (DateTime.UtcNow - _lastImport >= _importInterval)
                {
                    _lastImport = DateTime.UtcNow;
                    await ImportOnceAsync(stoppingToken);
                }
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

    // ── Sync: pending → סטטוס מהספק ───────────────────────────────────────────
    private async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var pending = await _supabase.GetPendingTemplatesAsync();

        var phones = new Dictionary<Guid, Phone?>();
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

            using var doc = JsonDocument.Parse(raw);
            var rawStatus = GetString(doc.RootElement, "status");
            if (rawStatus == null) { noAnswer++; continue; }

            var status = MapStatus(rawStatus);
            if (status == tpl.Status) { unchanged++; continue; }

            var reason    = GetString(doc.RootElement, "rejected_reason");
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

        _logger.LogInformation(
            "[TEMPLATE-SYNC] pending={Pending} updated={Updated} unchanged={Unchanged} " +
            "noProviderId={NoId} noContainer={NoContainer} noAnswer={NoAnswer}",
            pending.Count, updated, unchanged, noProviderId, noContainer, noAnswer);

        return updated;
    }

    // ── Import: קטלוג הספק → רשומות חסרות ────────────────────────────────────
    private async Task<int> ImportOnceAsync(CancellationToken ct)
    {
        var phones = await _supabase.GetAllPhonesAsync();
        int created = 0, linked = 0, known = 0, noContainer = 0, noAnswer = 0;

        foreach (var phone in phones)
        {
            if (ct.IsCancellationRequested) break;

            var provider = string.IsNullOrWhiteSpace(phone.Provider) ? "baileys" : phone.Provider;
            if (provider != "baileys") continue;
            if (string.IsNullOrEmpty(phone.DockerUrl)) { noContainer++; continue; }

            var existing = await _supabase.GetPhoneTemplatesAsync(phone.Id);
            var byKey = new Dictionary<string, PhoneTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in existing) byKey[LogicalKey(t.Name, t.Lang)] = t;

            foreach (var wanted in new[] { "APPROVED", "REJECTED" })
            {
                var raw = await GetFromContainer(
                    phone.DockerUrl, $"/templates?status={wanted}&limit=100", ct);
                if (raw == null) { noAnswer++; continue; }

                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in arr.EnumerateArray())
                {
                    var name = GetString(item, "name");
                    var lang = GetString(item, "language") ?? "en_US";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var key        = LogicalKey(name, lang);
                    var status     = MapStatus(GetString(item, "status"));
                    var providerId = GetString(item, "id");
                    var reason     = status == "rejected" ? GetString(item, "rejected_reason") : null;

                    // קיימת — רק משלימים provider id לטיוטה שרישומה נכשל
                    if (byKey.TryGetValue(key, out var local))
                    {
                        if (string.IsNullOrEmpty(local.ProviderTemplateId) && !string.IsNullOrEmpty(providerId))
                        {
                            local.ProviderTemplateId = providerId;
                            local.Status             = status;
                            local.IsPublished        = local.IsPublished || status == "approved";
                            local.RejectedReason     = reason;
                            local.UpdatedAt          = DateTime.UtcNow;

                            await _supabase.UpdatePhoneTemplateAsync(local);
                            _logger.LogInformation("[TEMPLATE-IMPORT] ↻ backfill {Name}/{Lang} providerId={ProviderId} | phoneId={PhoneId}",
                                name, lang, providerId, phone.Id);
                            linked++;
                        }
                        else known++;
                        continue;
                    }

                    // מונע כפילות בתוך אותו pass (APPROVED ואז REJECTED)
                    byKey[key] = new PhoneTemplate { Name = name, Lang = lang };

                    var content = ToTemplateContent(item);
                    var now     = DateTime.UtcNow;

                    try
                    {
                        var saved = await _supabase.CreatePhoneTemplateAsync(new PhoneTemplate
                        {
                            PhoneId            = phone.Id,
                            Name               = name,
                            Lang               = lang,
                            Category           = GetString(item, "category") ?? "UTILITY",
                            Content            = content,
                            ParamCount         = MaxParamIndex(content.Body?.Text),
                            Status             = status,
                            IsPublished        = status == "approved",   // מאושרת אצל הספק → ניתנת לשליחה
                            ProviderTemplateId = providerId,
                            RejectedReason     = reason,
                            CreatedAt          = now,
                            UpdatedAt          = now,
                        });

                        _logger.LogInformation("[TEMPLATE-IMPORT] + {Name}/{Lang} status={Status} | phoneId={PhoneId} templateId={Id} providerId={ProviderId}",
                            name, lang, status, phone.Id, saved.Id, providerId);
                        created++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[TEMPLATE-IMPORT] ✗ {Name}/{Lang} | phoneId={PhoneId}",
                            name, lang, phone.Id);
                    }
                }
            }
        }

        _logger.LogInformation(
            "[TEMPLATE-IMPORT] phones={Phones} created={Created} linked={Linked} known={Known} " +
            "noContainer={NoContainer} noAnswer={NoAnswer}",
            phones.Count, created, linked, known, noContainer, noAnswer);

        return created;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

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

    /// <summary>המזהה הלוגי של תבנית — name+lang, כמו ב-WhatsApp.</summary>
    private static string LogicalKey(string? name, string? lang) =>
        $"{(name ?? "").Trim().ToLowerInvariant()}|{(lang ?? "").Trim().ToLowerInvariant()}";

    private static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
