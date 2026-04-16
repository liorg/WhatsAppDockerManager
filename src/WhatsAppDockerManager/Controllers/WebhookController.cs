using Microsoft.AspNetCore.Mvc;
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
        
        // Save creds_base64 if provided
        if (!string.IsNullOrEmpty(payload.CredsB64))
        {
            await _supabaseService.UpdatePhoneCredsAsync(phoneId, payload.CredsB64);
            _logger.LogInformation("Saved creds_base64 for phone {PhoneId} (length: {Length})", phoneId, payload.CredsB64.Length);
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
            // Extract contact number from JID
            var contactNumber = payload.Jid.Split('@')[0];
            
            // Extract contact info from payload
            string? contactName = null;
            string? contactLid = null;

            if (payload.Data != null)
            {
                if (payload.Data.TryGetValue("pushName", out var pushName))
                    contactName = pushName?.ToString();
                if (payload.Data.TryGetValue("lid", out var lid))
                    contactLid = lid?.ToString();
            }
            // נסה למצוא ולקשר PingSender לפי LID


            // Upsert contact - create if not exists, update if exists
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
            // Determine message direction
            bool isIncoming = true;
           if (payload.Data?.TryGetValue("fromMe", out var fromMe) == true)
                {
                    if (fromMe is System.Text.Json.JsonElement jsonElement)
                        isIncoming = !jsonElement.GetBoolean();
                    else
                        isIncoming = !Convert.ToBoolean(fromMe);
                }

            // Build message content
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

            // Add type from payload if not in data
            if (!messageContent.ContainsKey("type") && !string.IsNullOrEmpty(payload.Type))
                messageContent["type"] = payload.Type;

            // Determine sender
            var sender = isIncoming ? contactNumber : phone.Number;

            // Save message
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

// Webhook DTOs
public class ContainerEventPayload
{
    public string? Event { get; set; }
    public string? MessageId { get; set; }
    public string? Jid { get; set; }
    public string? Type { get; set; }
    public Dictionary<string, object>? Data { get; set; }
    public long? Timestamp { get; set; }
    public string? Phone { get; set; }
    public string? Name { get; set; }
    public string? CredsB64 { get; set; }
}