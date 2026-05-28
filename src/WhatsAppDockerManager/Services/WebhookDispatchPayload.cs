// Services/WebhookDispatcherService.cs
using System.Text;
using System.Text.Json;
using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services;

public class WebhookDispatchPayload
{
    public string MessageId  { get; set; } = "";
    public string PhoneId    { get; set; } = "";
    public string ContactId  { get; set; } = "";
    public bool   Direction  { get; set; }
}

public interface IWebhookDispatcherService
{
    Task DispatchAsync(Guid phoneId, Guid contactId, Guid messageId, bool direction);
}

public class WebhookDispatcherService : IWebhookDispatcherService
{
    private readonly ISupabaseService  _supabase;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WebhookDispatcherService> _logger;

    public WebhookDispatcherService(
        ISupabaseService supabase,
        IHttpClientFactory httpFactory,
        ILogger<WebhookDispatcherService> logger)
    {
        _supabase    = supabase;
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    public async Task DispatchAsync(Guid phoneId, Guid contactId, Guid messageId, bool direction)
    {
        try
        {
            _logger.LogInformation("[DISPATCH] phone={PhoneId} contact={ContactId} message={MessageId} direction={Direction}",
                phoneId, contactId, messageId, direction ? "outgoing" : "incoming");

            // ── שלוף registrations דרך ISupabaseService ──────────────────
            var registrations = await _supabase.GetWebhookRegistrationsAsync(phoneId, contactId);
            if (!registrations.Any())
                return;

            var payload = JsonSerializer.Serialize(new WebhookDispatchPayload
            {
                MessageId = messageId.ToString(),
                PhoneId   = phoneId.ToString(),
                ContactId = contactId.ToString(),
                Direction = direction,
            });

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);

            foreach (var reg in registrations)
            {
                try
                {
                    var response = await http.PostAsync(
                        reg.CallbackUrl,
                        new StringContent(payload, Encoding.UTF8, "application/json"));

                    _logger.LogInformation("[DISPATCH] → {Url} status={Status}",
                        reg.CallbackUrl, (int)response.StatusCode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DISPATCH] Failed to notify {Url}", reg.CallbackUrl);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DISPATCH] Error for phone={PhoneId} contact={ContactId}",
                phoneId, contactId);
        }
    }
}