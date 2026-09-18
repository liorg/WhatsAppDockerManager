using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;

namespace WhatsAppDockerManager.Controllers;

/// <summary>
/// רישום תבניות מול ה-provider: יצירה + סנכרון סטטוס.
/// שיגור תבנית ללקוח — SendController.SendTemplate.
/// </summary>
[ApiController]
[Route("api/phones/{phoneId:guid}/templates")]
public class TemplatesController : ControllerBase
{
    private readonly ISupabaseService   _supabaseService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TemplatesController> _logger;

    public TemplatesController(
        ISupabaseService   supabaseService,
        IHttpClientFactory httpClientFactory,
        ILogger<TemplatesController> logger)
    {
        _supabaseService   = supabaseService;
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }
// ── deleteTemplate → FastAPI DELETE /templates/{id} ──────────────────────
    [HttpDelete("{templateId}")]
    public async Task<IActionResult> DeleteTemplate(Guid phoneId, string templateId)
    {
        var (phone, error) = await ResolvePhone(phoneId);
        if (error != null) return error;

        var template = await _supabaseService.GetPhoneTemplateByProviderIdAsync(phoneId, templateId);

        var (code, raw) = await CallContainer(
            phone!.DockerUrl!, HttpMethod.Delete, $"/templates/{Uri.EscapeDataString(templateId)}");

        // 404 מהקונטיינר = כבר לא קיים שם → עדיין מנקים את הרשומה
        if (code is (< 200 or >= 300) and not 404)
            return JsonRaw(code, raw);

        if (template == null)
        {
            _logger.LogWarning("[TEMPLATE-REG] Delete: no DB row | phoneId={PhoneId} providerId={ProviderId}",
                phoneId, templateId);
            return NotFound(new { error = "Template not found in DB", id = templateId, containerDeleted = true });
        }

        try
        {
            await _supabaseService.DeletePhoneTemplateAsync(template.Id);
            _logger.LogInformation("[TEMPLATE-REG] ✓ deleted {Name}/{Lang} | phoneId={PhoneId} templateId={Id} providerId={ProviderId}",
                template.Name, template.Lang, phoneId, template.Id, templateId);

            return Ok(new { success = true, id = templateId, phoneTemplateId = template.Id, name = template.Name, lang = template.Lang });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TEMPLATE-REG] ✗ DB delete failed — נמחק בקונטיינר בלבד | phoneId={PhoneId} providerId={ProviderId}",
                phoneId, templateId);
            return StatusCode(500, new { error = "Deleted in container but DB delete failed: " + ex.Message, id = templateId });
        }
    }
    // ── createTemplate → FastAPI POST /templates ──────────────────────────────
    [HttpPost]
    public async Task<IActionResult> CreateTemplate(Guid phoneId, [FromBody] JsonElement body)
    {
        var (phone, error) = await ResolvePhone(phoneId);
        if (error != null) return error;

        var name = GetString(body, "name");
        var lang = GetString(body, "language");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(lang))
            return BadRequest(new { error = "name and language are required" });

        // UNIQUE(phone_id, name, lang): כבר נרשם → 409 | טיוטה בלי provider id → רושמים אותה
        var existing = await _supabaseService.GetTemplateAsync(phoneId, name, lang);
        if (existing != null && existing.Lang != lang)
            existing = null;   // GetTemplateAsync נופל לשפה אחרת — כאן צריך התאמה מדויקת
        if (existing?.ProviderTemplateId != null)
            return Conflict(new { error = $"Template '{name}' ({lang}) already registered", id = existing.ProviderTemplateId, status = existing.Status });

        var (code, raw) = await CallContainer(phone!.DockerUrl!, HttpMethod.Post, "/templates", body);
        if (code is < 200 or >= 300) return JsonRaw(code, raw);

        using var doc  = JsonDocument.Parse(raw);
        var providerId = GetString(doc.RootElement, "id");
        var status     = MapStatus(GetString(doc.RootElement, "status"));
        var category   = GetString(doc.RootElement, "category");

        var content = ToTemplateContent(body);
        var now     = DateTime.UtcNow;
        try
        {
            PhoneTemplate saved;
            if (existing != null)
            {
                existing.Category           = category ?? existing.Category;
                existing.Content            = content;
                existing.ParamCount         = MaxParamIndex(content.Body?.Text);
                existing.Status             = status;
                existing.ProviderTemplateId = providerId;
                existing.RejectedReason     = null;
                existing.UpdatedAt          = now;
                saved = await _supabaseService.UpdatePhoneTemplateAsync(existing);
            }
            else
            {
                saved = await _supabaseService.CreatePhoneTemplateAsync(new PhoneTemplate
                {
                    PhoneId            = phoneId,
                    Name               = name,
                    Lang               = lang,
                    Category           = category ?? "UTILITY",
                    Content            = content,
                    ParamCount         = MaxParamIndex(content.Body?.Text),
                    Status             = status,
                    ProviderTemplateId = providerId,
                    CreatedAt          = now,
                    UpdatedAt          = now,
                });
            }

            _logger.LogInformation("[TEMPLATE-REG] ✓ {Name}/{Lang} | phoneId={PhoneId} templateId={Id} providerId={ProviderId} fromDraft={Draft}",
                name, lang, phoneId, saved.Id, providerId, existing != null);

            return Ok(new { id = providerId, status = status.ToUpperInvariant(), category, phoneTemplateId = saved.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TEMPLATE-REG] ✗ DB save failed — orphan in container | phoneId={PhoneId} providerId={ProviderId}",
                phoneId, providerId);
            return StatusCode(500, new { error = "Registered in container but DB save failed: " + ex.Message, id = providerId });
        }
    }

    // ── FindStatusTemplate → FastAPI GET /templates/{id} ─────────────────────
    [HttpGet("{templateId}")]
    public async Task<IActionResult> FindStatusTemplate(Guid phoneId, string templateId)
    {
        var (phone, error) = await ResolvePhone(phoneId);
        if (error != null) return error;

        var (code, raw) = await CallContainer(phone!.DockerUrl!, HttpMethod.Get, $"/templates/{Uri.EscapeDataString(templateId)}");
        if (code is < 200 or >= 300) return JsonRaw(code, raw);

        using var doc      = JsonDocument.Parse(raw);
        var rawStatus      = GetString(doc.RootElement, "status");
        var rejectedReason = GetString(doc.RootElement, "rejected_reason");

        var template = await _supabaseService.GetPhoneTemplateByProviderIdAsync(phoneId, templateId);
        if (template == null)
        {
            _logger.LogWarning("[TEMPLATE-REG] No DB row | phoneId={PhoneId} providerId={ProviderId}", phoneId, templateId);
        }
        else if (rawStatus != null && template.Status != MapStatus(rawStatus))
        {
            var status = MapStatus(rawStatus);
            await _supabaseService.UpdatePhoneTemplateStatusAsync(template.Id, status, rejectedReason);
            _logger.LogInformation("[TEMPLATE-REG] Status {Old} → {New} | {Name}/{Lang} templateId={Id}",
                template.Status, status, template.Name, template.Lang, template.Id);
        }

        return JsonRaw(code, raw);
    }
[HttpPost("validate")]
public async Task<IActionResult> ValidateTemplate(Guid phoneId, [FromBody] JsonElement body)
{
    var (phone, error) = await ResolvePhone(phoneId);
    if (error != null) return error;

    var name = GetString(body, "name");
    var lang = GetString(body, "language");
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(lang))
        return BadRequest(new { error = "name and language are required" });

    var content  = ToTemplateContent(body);
    var existing = await _supabaseService.GetTemplateAsync(phoneId, name, lang);

    return Ok(new
    {
        valid         = content.Body?.Text is { Length: > 0 },
        alreadyExists = existing?.ProviderTemplateId != null,
        paramCount    = MaxParamIndex(content.Body?.Text),
        parsedBody    = content.Body?.Text,
    });
}

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<(Phone? phone, IActionResult? error)> ResolvePhone(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return (null, NotFound(new { error = "Phone not found" }));
        if (string.IsNullOrEmpty(phone.DockerUrl))
            return (null, BadRequest(new { error = "Container not running", dockerStatus = phone.DockerStatus }));

        var provider = string.IsNullOrWhiteSpace(phone.Provider) ? "baileys" : phone.Provider;
        if (provider != "baileys")
            return (null, StatusCode(501, new { error = "Template registration is implemented for baileys only", provider }));

        return (phone, null);
    }

    private async Task<(int code, string raw)> CallContainer(string dockerUrl, HttpMethod method, string path, object? body = null)
    {
        var url = $"{dockerUrl}{path}";
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            using var req = new HttpRequestMessage(method, url);
            if (body != null) req.Content = JsonContent.Create(body);

            using var res = await client.SendAsync(req);
            var raw = await res.Content.ReadAsStringAsync();
            _logger.LogInformation("[TEMPLATE-REG] {Method} {Url} → {Status} {Body}", method, url, (int)res.StatusCode, raw);
            return ((int)res.StatusCode, raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TEMPLATE-REG] Container unavailable | url={Url}", url);
            return (503, JsonSerializer.Serialize(new { error = "Container unavailable", details = ex.Message }));
        }
    }

    private static ContentResult JsonRaw(int code, string raw) =>
        new() { Content = raw, ContentType = "application/json", StatusCode = code };

    private static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    // WhatsApp components[] → TemplateContent {header, body, footer, buttons} — המבנה ש-SendTemplate קורא
    private static TemplateContent ToTemplateContent(JsonElement body)
    {
        var content = new TemplateContent();
        if (!body.TryGetProperty("components", out var comps) || comps.ValueKind != JsonValueKind.Array)
            return content;

        foreach (var comp in comps.EnumerateArray())
        {
            var text = GetString(comp, "text");
            switch (GetString(comp, "type")?.ToUpperInvariant())
            {
                case "HEADER":
                    content.Header = new TemplateHeader { Text = text, Format = GetString(comp, "format")?.ToLowerInvariant() ?? "text" };
                    break;
                case "BODY":
                    content.Body = new TemplatePart { Text = text };
                    break;
                case "FOOTER":
                    content.Footer = new TemplatePart { Text = text };
                    break;
                case "BUTTONS":
                    if (comp.TryGetProperty("buttons", out var btns) && btns.ValueKind == JsonValueKind.Array)
                        content.Buttons = btns.EnumerateArray()
                            .Select(b => new TemplateButton { Type = GetString(b, "type")?.ToLowerInvariant(), Text = GetString(b, "text") })
                            .ToList();
                    break;
            }
        }
        return content;
    }

    // זהה ל-SendController.MaxParamIndex — כמה ערכים השיגור ידרוש
    private static readonly Regex ParamRe = new(@"\{\{\s*(\d+)\s*\}\}", RegexOptions.Compiled);

    private static int MaxParamIndex(string? text) =>
        string.IsNullOrEmpty(text) ? 0
            : ParamRe.Matches(text).Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0).DefaultIfEmpty(0).Max();

    // WhatsApp PENDING/APPROVED/REJECTED/PAUSED → pending | approved | rejected | pause
    private static string MapStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "APPROVED" => "approved",
        "REJECTED" => "rejected",
        "PAUSED"   => "pause",
        _          => "pending",
    };
}
