using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Services;

namespace WhatsAppDockerManager.Controllers;

/// <summary>
/// Contacts Controller - Get contacts for a phone
/// </summary>
[ApiController]
[Route("api/phones/{phoneId}/contacts")]
public class ContactsController : ControllerBase
{
    private readonly ISupabaseService _supabaseService;
    private readonly ILogger<ContactsController> _logger;

    public ContactsController(ISupabaseService supabaseService, ILogger<ContactsController> logger)
    {
        _supabaseService = supabaseService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetContacts(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });

        var contacts = await _supabaseService.GetContactsForPhoneAsync(phoneId);
        return Ok(new
        {
            phoneId = phoneId,
            count = contacts.Count,
            contacts = contacts.Select(c => new
            {
                id = c.Id,
                number = c.Number,
                name = c.Name,
                tag = c.Tag,
                isBot = c.IsBot,
                isConnect = c.IsConnect,
                createdAt = c.CreatedAt
            })
        });
    }

    [HttpGet("{contactId}")]
    public async Task<IActionResult> GetContact(Guid phoneId, Guid contactId)
    {
        var contact = await _supabaseService.GetContactByIdAsync(contactId);
        if (contact == null || contact.PhoneId != phoneId)
            return NotFound(new { error = "Contact not found" });

        return Ok(contact);
    }
}
