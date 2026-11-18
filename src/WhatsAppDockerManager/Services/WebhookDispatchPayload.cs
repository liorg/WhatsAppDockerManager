// Services/WebhookDispatcherService.cs
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhatsAppDockerManager.Controllers;
using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services;

/// <summary>
/// kind: "whatsapp" (ברירת מחדל) או "sms". צרכן שלא מכיר את השדה ממשיך לעבוד
/// כמו קודם על הודעות WhatsApp; SMS מגיע במבנה נפרד (WebhookSmsDispatchPayload).
/// </summary>
public class WebhookDispatchPayload
{
    [JsonPropertyName("kind")]              public string Kind      { get; set; } = "whatsapp";
    [JsonPropertyName("messageId")]         public string MessageId { get; set; } = "";
    [JsonPropertyName("phoneId")]           public string PhoneId   { get; set; } = "";
    [JsonPropertyName("contactId")]         public string ContactId { get; set; } = "";
    [JsonPropertyName("whatsAppMessageId")] public string WhatsAppMessageId { get; set; } = "";
    [JsonPropertyName("direction")]         public bool   Direction { get; set; }
}

/// <summary>
/// SMS אין לו contact ואין לו message — רק טלפון. ה-Worker מנתב לפי phoneId
/// ומחליט בעצמו איזה סשן ב-wait_sms צורך אותו.
/// </summary>
public class WebhookSmsDispatchPayload
{
    [JsonPropertyName("kind")]    public string Kind    { get; set; } = "sms";
    [JsonPropertyName("phoneId")] public string PhoneId { get; set; } = "";
    [JsonPropertyName("sms")]     public SmsEventPayload Sms { get; set; } = new();
}

public interface IWebhookDispatcherService
{
    Task DispatchAsync(Guid phoneId, Guid contactId, Guid messageId,
                       string whatsAppMessageId,
                       bool direction);

    Task DispatchSmsAsync(Guid phoneId, SmsEventPayload sms);
}

public class WebhookDispatcherService : IWebhookDispatcherService
{
    private readonly ISupabaseService           _supabase;
    private readonly IHttpClientFactory         _httpFactory;
    private readonly IConfiguration             _cfg;
    private readonly ILogger<WebhookDispatcherService> _logger;

    private static readonly string[] Priority =
    {
        WebhookRegistrationType.Trigger,
        WebhookRegistrationType.Job,
        WebhookRegistrationType.Recording,
    };

    // SMS רק ל-Spine (Trigger → /incoming). Job / Recording לא מכירים kind=sms.
    private static readonly string[] SmsTypes =
    {
        WebhookRegistrationType.Trigger,
    };

    public WebhookDispatcherService(
        ISupabaseService supabase,
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ILogger<WebhookDispatcherService> logger)
    {
        _cfg         = cfg;
        _supabase    = supabase;
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    public async Task DispatchAsync(Guid phoneId, Guid contactId, Guid messageId,
        string whatsAppMessageId,
        bool direction)
    {
        try
        {
            _logger.LogInformation(
                "[DISPATCH] phone={PhoneId} contact={ContactId} message={MessageId} direction={Direction}",
                phoneId, contactId, messageId, direction ? "outgoing" : "incoming");

            var payload = JsonSerializer.Serialize(new WebhookDispatchPayload
            {
                MessageId         = messageId.ToString(),
                PhoneId           = phoneId.ToString(),
                ContactId         = contactId.ToString(),
                Direction         = direction,
                WhatsAppMessageId = whatsAppMessageId,
            });

            await PostToAllAsync(payload, "WA", Priority);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DISPATCH] Error for phone={PhoneId} contact={ContactId}",
                phoneId, contactId);
        }
    }

    public async Task DispatchSmsAsync(Guid phoneId, SmsEventPayload sms)
    {
        try
        {
            _logger.LogInformation("[DISPATCH][SMS] phone={PhoneId} msg={MsgId} from={From}",
                phoneId, sms.MsgId, sms.From);

            var payload = JsonSerializer.Serialize(new WebhookSmsDispatchPayload
            {
                PhoneId = phoneId.ToString(),
                Sms     = sms,
            });

            await PostToAllAsync(payload, "SMS", SmsTypes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DISPATCH][SMS] Error for phone={PhoneId}", phoneId);
        }
    }

    // ── משותף: אותם webhooks רשומים, אותו סדר עדיפויות ──────────────────────
    private async Task PostToAllAsync(string payload, string tag, string[] types)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(5);

        // אותו סוד משותף כמו sms-event (נכנס). backend recording callback בודק אותו;
        // Spine מתעלם מ-header שהוא לא מכיר.
        var secret = _cfg["AppSettings:WebhookSecret"] is { Length: > 0 } ws ? ws : "manager-secret";

        foreach (var eventType in types)
        {
            var registrations = await _supabase.GetActiveWebhooksByTypeAsync(eventType);
            if (!registrations.Any())
                continue;

            foreach (var reg in registrations)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, reg.CallbackUrl)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                    };
                    req.Headers.Add("X-Webhook-Secret", secret);

                    using var response = await http.SendAsync(req);

                    _logger.LogInformation("[DISPATCH][{Tag}][{Type}] → {Url} status={Status}",
                        tag, eventType.ToUpper(), reg.CallbackUrl, (int)response.StatusCode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DISPATCH][{Tag}][{Type}] Failed to notify {Url}",
                        tag, eventType.ToUpper(), reg.CallbackUrl);
                }
            }
        }
    }
}
