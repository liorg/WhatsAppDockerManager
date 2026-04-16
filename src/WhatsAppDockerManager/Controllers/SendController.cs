using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;
using System.Text.Json;

namespace WhatsAppDockerManager.Controllers;

/// <summary>
/// Send Controller - Wrapper for sending messages (Docker not exposed)
/// </summary>
[ApiController]
[Route("api/phones/{phoneId}/send")]
public class SendController : ControllerBase
{
    private readonly ISupabaseService _supabaseService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SendController> _logger;

    public SendController(
        ISupabaseService supabaseService,
        IHttpClientFactory httpClientFactory,
        ILogger<SendController> logger)
    {
        _supabaseService = supabaseService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Send text message
    /// </summary>
    [HttpPost("text")]
    public async Task<IActionResult> SendText(Guid phoneId, [FromBody] SendTextRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/text", request, request.Jid, new { text = request.Text });
    }

    /// <summary>
    /// Send buttons (up to 3)
    /// </summary>
    [HttpPost("buttons")]
    public async Task<IActionResult> SendButtons(Guid phoneId, [FromBody] SendButtonsRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/buttons", request, request.Jid, 
            new { type = "buttons", text = request.Text, buttons = request.Buttons });
    }

    /// <summary>
    /// Send list menu
    /// </summary>
    [HttpPost("list")]
    public async Task<IActionResult> SendList(Guid phoneId, [FromBody] SendListRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/list", request, request.Jid, 
            new { type = "list", text = request.Text, sections = request.Sections });
    }

    /// <summary>
    /// Send button response (simulate button click)
    /// </summary>
    [HttpPost("button-response")]
    public async Task<IActionResult> SendButtonResponse(Guid phoneId, [FromBody] SendButtonResponseRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/button-response", request, request.Jid, 
            new { type = "button_response", buttonId = request.ButtonId });
    }

    /// <summary>
    /// Send list response (simulate list selection)
    /// </summary>
    [HttpPost("list-response")]
    public async Task<IActionResult> SendListResponse(Guid phoneId, [FromBody] SendListResponseRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/list-response", request, request.Jid, 
            new { type = "list_response", rowId = request.RowId });
    }

    /// <summary>
    /// Get WhatsApp connection status
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });

        if (string.IsNullOrEmpty(phone.DockerUrl))
            return BadRequest(new { error = "Container not running" });

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync($"{phone.DockerUrl}/status");
            var content = await response.Content.ReadAsStringAsync();
            return Content(content, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting status for phone {PhoneId}", phoneId);
            return StatusCode(503, new { error = "Container unavailable" });
        }
    }

    /// <summary>
    /// Get QR code for authentication
    /// </summary>
    [HttpGet("qrcode")]
    public async Task<IActionResult> GetQrCode(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });

        if (string.IsNullOrEmpty(phone.DockerUrl))
            return BadRequest(new { error = "Container not running" });

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync($"{phone.DockerUrl}/qrcode");
            var content = await response.Content.ReadAsStringAsync();
            return Content(content, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting QR for phone {PhoneId}", phoneId);
            return StatusCode(503, new { error = "Container unavailable" });
        }
    }

    /// <summary>
    /// Get QR code as image
    /// </summary>
    [HttpGet("qrcode/image")]
    public async Task<IActionResult> GetQrCodeImage(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });

        if (string.IsNullOrEmpty(phone.DockerUrl))
            return BadRequest(new { error = "Container not running" });

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync($"{phone.DockerUrl}/qrcode/image");
            var imageBytes = await response.Content.ReadAsByteArrayAsync();
            return File(imageBytes, "image/png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting QR image for phone {PhoneId}", phoneId);
            return StatusCode(503, new { error = "Container unavailable" });
        }
    }
/// <summary>
/// Send PING message to identify contact LID
/// </summary>
[HttpPost("ping")]
public async Task<IActionResult> SendPing(Guid phoneId, [FromBody] SendPingRequest request)
{
    var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
    if (phone == null)
        return NotFound(new { error = "Phone not found" });

    if (string.IsNullOrEmpty(phone.DockerUrl))
        return BadRequest(new { error = "Container not running" });

    try
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var pingText = request.Text ?? "🔔";
        var sendRequest = new { jid = request.Jid, text = pingText };
        
        var response = await client.PostAsJsonAsync($"{phone.DockerUrl}/send/text", sendRequest);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            string? whatsappMessageId = null;
            try
            {
                var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                if (jsonResponse.TryGetProperty("messageId", out var msgIdElement))
                    whatsappMessageId = msgIdElement.GetString();
            }
            catch { }

            var targetNumber = request.Jid.Split('@')[0];
            var pingSender = await _supabaseService.CreatePingSenderAsync(phoneId, targetNumber, whatsappMessageId);

            _logger.LogInformation("Sent PING to {Jid}, PingSender: {PingSenderId}", request.Jid, pingSender.Id);

            return Ok(new { 
                success = true, 
                pingSenderId = pingSender.Id,
                messageId = whatsappMessageId 
            });
        }

        return StatusCode((int)response.StatusCode, JsonSerializer.Deserialize<object>(responseContent));
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error sending PING for phone {PhoneId}", phoneId);
        return StatusCode(503, new { error = "Container unavailable", details = ex.Message });
    }
}
    private async Task<IActionResult> ForwardToContainer(Guid phoneId, string endpoint, object request, string jid, object messageContent)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });

        if (string.IsNullOrEmpty(phone.DockerUrl))
            return BadRequest(new { error = "Container not running", dockerStatus = phone.DockerStatus });

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            
            var response = await client.PostAsJsonAsync($"{phone.DockerUrl}{endpoint}", request);
            var responseContent = await response.Content.ReadAsStringAsync();

          if (response.IsSuccessStatusCode)
            {
                // Extract messageId from response
                string? whatsappMessageId = null;
                try
                {
                    var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                    if (jsonResponse.TryGetProperty("messageId", out var msgIdElement))
                        whatsappMessageId = msgIdElement.GetString();
                }
                catch { }

                await LogOutgoingMessage(phoneId, phone, jid, messageContent, whatsappMessageId);
                _logger.LogInformation("Sent message to {Jid} via phone {PhoneId}, messageId: {MessageId}", jid, phoneId, whatsappMessageId);
            }
            else
            {
                _logger.LogWarning("Failed to send message: {Status} - {Response}", response.StatusCode, responseContent);
            }

            return StatusCode((int)response.StatusCode, JsonSerializer.Deserialize<object>(responseContent));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message for phone {PhoneId}", phoneId);
            return StatusCode(503, new { error = "Container unavailable", details = ex.Message });
        }
    }

   private async Task LogOutgoingMessage(Guid phoneId, Phone phone, string jid, object content, string? whatsappMessageId)
    {
        try
        {
            var contactNumber = jid.Split('@')[0];
            var contact = await _supabaseService.UpsertContactAsync(phoneId, contactNumber);
            
           await _supabaseService.AddMessageAsync(
                    phoneId,
                    contact.Id,
                    phone.Number,
                    content,
                    direction: false,
                    leafId: null,
                    whatsappMessageId: whatsappMessageId
                );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging outgoing message");
        }
    }
}

// Send DTOs
public class SendTextRequest
{
    public string Jid { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public class ButtonItem
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public class SendButtonsRequest
{
    public string Jid { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? Footer { get; set; }
    public List<ButtonItem> Buttons { get; set; } = new();
}

public class ListRow
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class ListSection
{
    public string Title { get; set; } = string.Empty;
    public List<ListRow> Rows { get; set; } = new();
}

public class SendListRequest
{
    public string Jid { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string ButtonText { get; set; } = "בחר אפשרות";
    public string? Footer { get; set; }
    public List<ListSection> Sections { get; set; } = new();
}

public class SendButtonResponseRequest
{
    public string Jid { get; set; } = string.Empty;
    public string ButtonId { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
}

public class SendListResponseRequest
{
    public string Jid { get; set; } = string.Empty;
    public string RowId { get; set; } = string.Empty;
    public string? Title { get; set; }
}
public class SendPingRequest
{
    public string Jid { get; set; } = string.Empty;
    public string? Text { get; set; }
}