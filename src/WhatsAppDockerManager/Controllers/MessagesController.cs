using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Services;
using System.Text.Json;

namespace WhatsAppDockerManager.Controllers;

/// <summary>
/// Messages Controller - Get messages for a contact
/// </summary>
[ApiController]
[Route("api/phones/{phoneId}/contacts/{contactId}/messages")]
public class MessagesController : ControllerBase
{
    private readonly ISupabaseService _supabaseService;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(ISupabaseService supabaseService, ILogger<MessagesController> logger)
    {
        _supabaseService = supabaseService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetMessages(Guid phoneId, Guid contactId, [FromQuery] int limit = 100)
    {
        var contact = await _supabaseService.GetContactByIdAsync(contactId);
        if (contact == null || contact.PhoneId != phoneId)
            return NotFound(new { error = "Contact not found" });

        var messages = await _supabaseService.GetMessagesForContactAsync(contactId, limit);
        return Ok(new
        {
            phoneId = phoneId,
            contactId = contactId,
            count = messages.Count,
            messages = messages.Select(m => new
            {
                id = m.Id,
                sender = m.Sender,
                content = JsonSerializer.Deserialize<object>(m.Content),
                direction = m.Direction,
                status = m.Status,
                sentAt = m.SentAt,
                leafId = m.LeafId
            })
        });
    }
}
