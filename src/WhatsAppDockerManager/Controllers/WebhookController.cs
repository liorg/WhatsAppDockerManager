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
        _logger.LogInformation("Container event for phone {PhoneId}: {Event}", phoneId, payload.Event);
        _logger.LogInformation("Payload received: {@Payload}", payload);

        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });

        switch (payload.Event)
        {
            case "authenticated":
                _logger.LogInformation("Phone {PhoneId} authenticated as {Phone}", phoneId, payload.Phone);
                await _supabaseService.UpdatePhoneDockerStatusAsync(phoneId, PhoneDockerStatus.Running);
                if (!string.IsNullOrEmpty(payload.Phone))
                    await _supabaseService.UpdatePhoneNumberAsync(phoneId, payload.Phone);
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
             _logger.LogInformation("Phone {PhoneId} wmessage", phoneId);
                await HandleIncomingMessage(phoneId, phone, payload);
                break;
        }

        //if (payload.Event != "message")
       // {
        //      await _supabaseService.LogAgentEventAsync(
        //            _containerManager.CurrentHostId, 
       //           payload.Event ?? "unknown",
         //           new { phoneId = phone.Id, error = "Failed to create container" }
        //        );
        //    await _supabaseService.LogAgentEventAsync(phoneId, _containerManager.CurrentHostId, payload.Event ?? "unknown", payload);
       // }

        return Ok(new { received = true });
    }

    private async Task HandleIncomingMessage(Guid phoneId, Phone phone, ContainerEventPayload payload)
    {  
        _logger.LogInformation("HandleIncomingMessage ");//");
        if (string.IsNullOrEmpty(payload.Jid)) return;

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

            var contact = await _supabaseService.UpsertContactAsync(phoneId, contactNumber, name: contactName, lid: contactLid);

            bool isIncoming = true;
            if (payload.Data?.TryGetValue("fromMe", out var fromMe) == true)
                isIncoming = !Convert.ToBoolean(fromMe);

            var messageContent = new Dictionary<string, object>();
            if (payload.Data != null)
            {
                if (payload.Data.TryGetValue("text", out var text)) messageContent["text"] = text;
                if (payload.Data.TryGetValue("type", out var type)) messageContent["type"] = type;
                if (payload.Data.TryGetValue("buttonId", out var buttonId)) messageContent["buttonId"] = buttonId;
                if (payload.Data.TryGetValue("selectedId", out var selectedId)) messageContent["selectedId"] = selectedId;
            }

            if (messageContent.Count == 0 && !string.IsNullOrEmpty(payload.Type))
                messageContent["type"] = payload.Type;

            var sender = isIncoming ? contactNumber : phone.Number;

            await _supabaseService.AddMessageAsync(phoneId, contact.Id, sender, messageContent, direction: isIncoming, leafId: payload.MessageId);

            _logger.LogInformation("Saved message from {Sender} for phone {PhoneId}", sender, phoneId);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Error handling message for phone {PhoneId}", phoneId);
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
