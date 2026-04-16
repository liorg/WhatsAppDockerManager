using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;

namespace WhatsAppDockerManager.Controllers;

/// <summary>
/// Internal Webhook Controller - receives events from Docker containers
/// The Agent registers itself as webhook in each container automatically.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class WebhookController : ControllerBase
{
    private readonly IContainerManager _containerManager;
    private readonly ISupabaseService _supabaseService;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(
        IContainerManager containerManager,
        ISupabaseService supabaseService,
        ILogger<WebhookController> logger)
    {
        _containerManager = containerManager;
        _supabaseService = supabaseService;
        _logger = logger;
    }

    [HttpPost("container-event/{phoneId}")]
    public async Task<IActionResult> ContainerEvent(Guid phoneId, [FromBody] ContainerEventPayload payload)
    {
        _logger.LogInformation("Container event for phone {PhoneId}: {Event}", phoneId, payload.Event ?? "unknown");
        _logger.LogDebug("Payload received: {@Payload}", payload);

        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });

        switch (payload.Event)
        {
            case "authenticated":
                await HandleAuthenticated(phoneId, phone, payload);
                break;

            case "disconnected":
                _logger.LogWarning("Phone {PhoneId} disconnected", phoneId);
                await _supabaseService.UpdatePhoneDockerStatusAsync(phoneId, PhoneDockerStatus.Error, errorMessage: "WhatsApp disconnected");
                break;

            case "qr":
                _logger.LogInformation("Phone {PhoneId} waiting for QR scan", phoneId);
                await _supabaseService.UpdatePhoneDockerStatusAsync(phoneId, PhoneDockerStatus.Pending);
                break;

            case "message":
                _logger.LogInformation("Phone {PhoneId} received message", phoneId);
                await HandleIncomingMessage(phoneId, phone, payload);
                break;

            default:
                _logger.LogWarning("Unknown event type: {Event}", payload.Event);
                break;
        }

        return Ok(new { received = true });
    }

    /// <summary>
    /// Handle authenticated event - save creds and update phone number
    /// </summary>
    private async Task HandleAuthenticated(Guid phoneId, Phone phone, ContainerEventPayload payload)
    {
        _logger.LogInformation("Phone {PhoneId} authenticated as {Phone}", phoneId, payload.Phone);
        
        // Update status to running
        await _supabaseService.UpdatePhoneDockerStatusAsync(phoneId, PhoneDockerStatus.Running);
        
        // Update phone number if provided
        if (!string.IsNullOrEmpty(payload.Phone))
        {
            var normalizedPhone = "+" + payload.Phone.Replace("+", "");
            await _supabaseService.UpdatePhoneNumberAsync(phoneId, normalizedPhone);
        }
        
        // ── שמור creds_base64 ← הכי חשוב! ──────────────────────
        if (!string.IsNullOrEmpty(payload.CredsB64))
        {
            await _supabaseService.UpdatePhoneCredsAsync(phoneId, payload.CredsB64);
            _logger.LogInformation("Saved creds_base64 for phone {PhoneId} (length: {Length})", 
                phoneId, payload.CredsB64.Length);
        }
        else
        {
            _logger.LogWarning("authenticated event received but creds_b64 is empty for phone {PhoneId}", phoneId);
        }
    }

    /// <summary>
    /// Handle incoming message - create contact if not exists and save message
    /// </summary>
    private async Task HandleIncomingMessage(Guid phoneId, Phone phone, ContainerEventPayload payload)
    {
        if (string.IsNullOrEmpty(payload.Jid)) 
        {
            _logger.LogWarning("Message received without JID for phone {PhoneId}", phoneId);
            return;
        }

        try
        {
            var contactNumber = payload.Jid.Split('@')[0];
            
            string? contactName = null;
            string? contactLid = null;

            if (payload.Data != null)
            {
                if (payload.Data.TryGetValue("pushName", out var pushName))
                    contactName = pushName?.ToString();
                if (payload.Data.TryGetValue("lid", out var lid))
                    contactLid = lid?.ToString();
            }

            var contact = await _supabaseService.UpsertContactAsync(
                phoneId, 
                contactNumber, 
                name: contactName, 
                lid: contactLid
            );
            _logger.LogInformation("Contact upserted: {ContactId} ({Number})", contact.Id, contactNumber);

            if (!string.IsNullOrEmpty(contactLid))
            {
                await _supabaseService.MatchPingSenderByLidAsync(phoneId, contactLid, contact.Id);
            }

            bool isIncoming = true;
            if (payload.Data?.TryGetValue("fromMe", out var fromMe) == true)
            {
                if (fromMe is System.Text.Json.JsonElement jsonElement)
                    isIncoming = !jsonElement.GetBoolean();
                else
                    isIncoming = !Convert.ToBoolean(fromMe);
            }

            var messageContent = new Dictionary<string, object?>();
            if (payload.Data != null)
            {
                if (payload.Data.TryGetValue("text", out var text)) 
                    messageContent["text"] = text;
                if (payload.Data.TryGetValue("type", out var type)) 
                    messageContent["type"] = type;
                if (payload.Data.TryGetValue("buttonId", out var buttonId)) 
                    messageContent["buttonId"] = buttonId;
                if (payload.Data.TryGetValue("selectedId", out var selectedId)) 
                    messageContent["selectedId"] = selectedId;
                if (payload.Data.TryGetValue("caption", out var caption)) 
                    messageContent["caption"] = caption;
            }

            if (!messageContent.ContainsKey("type") && !string.IsNullOrEmpty(payload.Type))
                messageContent["type"] = payload.Type;

            var sender = isIncoming ? contactNumber : phone.Number;

            var message = await _supabaseService.AddMessageAsync(
                phoneId, 
                contact.Id, 
                sender, 
                messageContent, 
                direction: isIncoming, 
                leafId: null,
                whatsappMessageId: payload.MessageId
            );

            _logger.LogInformation("Saved message {MessageId} from {Sender} for phone {PhoneId}", 
                message.Id, sender, phoneId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message for phone {PhoneId}", phoneId);
        }
    }
}

// ── Webhook DTOs ──────────────────────────────────────────────────────────────
public class ContainerEventPayload
{
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("jid")]
    public string? Jid { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, object>? Data { get; set; }

    // ← שנה מ-long? ל-object?
    [JsonPropertyName("timestamp")]
    public object? Timestamp { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("creds_b64")]
    public string? CredsB64 { get; set; }
}