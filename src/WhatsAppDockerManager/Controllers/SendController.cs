using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;
using System.Text.Json;

namespace WhatsAppDockerManager.Controllers;

[ApiController]
[Route("api/phones/{phoneId}/send")]
public class SendController : ControllerBase
{
    private readonly ISupabaseService   _supabaseService;
    private readonly ISenderLogService  _senderLogService;   // ← חדש
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SendController> _logger;

    public SendController(
        ISupabaseService   supabaseService,
        ISenderLogService  senderLogService,               // ← inject
        IHttpClientFactory httpClientFactory,
        ILogger<SendController> logger)
    {
        _supabaseService   = supabaseService;
        _senderLogService  = senderLogService;
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }
    // ── Send image ──────────────────────────────────────────────────────────────
    [HttpPost("image")]
    public async Task<IActionResult> SendImage(Guid phoneId, [FromBody] SendImageRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/image", request, request.Jid,
            "image", new { caption = request.Caption, mimetype = request.Mimetype });
    }

    // ── Send sticker ────────────────────────────────────────────────────────────
    [HttpPost("sticker")]
    public async Task<IActionResult> SendSticker(Guid phoneId, [FromBody] SendStickerRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/sticker", request, request.Jid,
            "sticker", new { });
    }
    
    // ── Send text ─────────────────────────────────────────────────────────────
    [HttpPost("text")]
    public async Task<IActionResult> SendText(Guid phoneId, [FromBody] SendTextRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/text", request, request.Jid,
            "text", new { text = request.Text });
    }

    // ── Send buttons ──────────────────────────────────────────────────────────
    [HttpPost("buttons")]
    public async Task<IActionResult> SendButtons(Guid phoneId, [FromBody] SendButtonsRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/buttons", request, request.Jid,
            "buttons", new { text = request.Text, buttons = request.Buttons, footer = request.Footer });
    }

    // ── Send list ─────────────────────────────────────────────────────────────
    [HttpPost("list")]
    public async Task<IActionResult> SendList(Guid phoneId, [FromBody] SendListRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/list", request, request.Jid,
            "list", new { text = request.Text, title = request.Title, sections = request.Sections });
    }

    // ── Send button-response ──────────────────────────────────────────────────
    [HttpPost("button-response")]
    public async Task<IActionResult> SendButtonResponse(Guid phoneId, [FromBody] SendButtonResponseRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/button-response", request, request.Jid,
            "button_response", new { buttonId = request.ButtonId, displayText = request.DisplayText });
    }

    // ── Send list-response ────────────────────────────────────────────────────
    [HttpPost("list-response")]
    public async Task<IActionResult> SendListResponse(Guid phoneId, [FromBody] SendListResponseRequest request)
    {
        return await ForwardToContainer(phoneId, "/send/list-response", request, request.Jid,
            "list_response", new { rowId = request.RowId, title = request.Title });
    }

    // ── Send ping ─────────────────────────────────────────────────────────────
    [HttpPost("ping")]
    public async Task<IActionResult> SendPing(Guid phoneId, [FromBody] SendPingRequest request)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)          return NotFound(new { error = "Phone not found" });
        if (string.IsNullOrEmpty(phone.DockerUrl))
            return BadRequest(new { error = "Container not running" });

        string? whatsappMessageId = null;
        var targetNumber = request.Jid.Split('@')[0];

        try
        {
            var pingSender = await _supabaseService.CreatePingSenderAsync(
                phoneId, targetNumber, null, phone.UserId);

            _logger.LogInformation("[PING] Created ping_sender {PsId}", pingSender.Id);

            var client     = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var sendReq    = new { jid = request.Jid, text = request.Text ?? "🔔" };

            var response        = await client.PostAsJsonAsync($"{phone.DockerUrl}/send/text", sendReq);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // ── log כישלון ────────────────────────────────────
                await _senderLogService.LogAsync(phoneId, request.Jid, "ping",
                    sendReq, status: "failed",
                    errorMessage: $"{(int)response.StatusCode}: {responseContent}");

                return StatusCode((int)response.StatusCode,
                    JsonSerializer.Deserialize<object>(responseContent));
            }

            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(responseContent);
                if (json.TryGetProperty("messageId", out var msgId))
                    whatsappMessageId = msgId.GetString();
            }
            catch { }

            if (!string.IsNullOrEmpty(whatsappMessageId))
            {
                pingSender.PingMessageId = whatsappMessageId;
                await _supabaseService.UpdatePingSenderAsync(pingSender);
            }

            var contact = await _supabaseService.GetContactByNumberAsync(phoneId, targetNumber);
            if (contact != null && pingSender.ContactId == null)
            {
                pingSender.ContactId = contact.Id;
                await _supabaseService.UpdatePingSenderAsync(pingSender);
            }

            // ── log הצלחה ─────────────────────────────────────────
            await _senderLogService.LogAsync(phoneId, request.Jid, "ping",
                sendReq, whatsappMessageId: whatsappMessageId);

            return Ok(new
            {
                success      = true,
                pingSenderId = pingSender.Id,
                messageId    = whatsappMessageId,
                contactId    = contact?.Id,
            });
        }
        catch (Exception ex)
        {
            await _senderLogService.LogAsync(phoneId, request.Jid, "ping",
                new { text = request.Text }, status: "failed", errorMessage: ex.Message);

            _logger.LogError(ex, "[PING] Error for phone {PhoneId}", phoneId);
            return StatusCode(503, new { error = "Container unavailable", details = ex.Message });
        }
    }

    // ── Status / QR (ללא שינוי) ───────────────────────────────────────────────
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null) return NotFound(new { error = "Phone not found" });
        if (string.IsNullOrEmpty(phone.DockerUrl)) return BadRequest(new { error = "Container not running" });
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync($"{phone.DockerUrl}/status");
            return Content(await response.Content.ReadAsStringAsync(), "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting status for phone {PhoneId}", phoneId);
            return StatusCode(503, new { error = "Container unavailable" });
        }
    }

    [HttpGet("qrcode")]
    public async Task<IActionResult> GetQrCode(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null) return NotFound(new { error = "Phone not found" });
        if (string.IsNullOrEmpty(phone.DockerUrl)) return BadRequest(new { error = "Container not running" });
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync($"{phone.DockerUrl}/qrcode");
            return Content(await response.Content.ReadAsStringAsync(), "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting QR for phone {PhoneId}", phoneId);
            return StatusCode(503, new { error = "Container unavailable" });
        }
    }

    [HttpGet("qrcode/image")]
    public async Task<IActionResult> GetQrCodeImage(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null) return NotFound(new { error = "Phone not found" });
        if (string.IsNullOrEmpty(phone.DockerUrl)) return BadRequest(new { error = "Container not running" });
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync($"{phone.DockerUrl}/qrcode/image");
            return File(await response.Content.ReadAsByteArrayAsync(), "image/png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting QR image for phone {PhoneId}", phoneId);
            return StatusCode(503, new { error = "Container unavailable" });
        }
    }

    // ── ForwardToContainer — רושם ל-sender_log, לא ל-messages ───────────────
    private async Task<IActionResult> ForwardToContainer(
        Guid phoneId, string endpoint, object request,
        string jid, string messageType, object logContent)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });
        if (string.IsNullOrEmpty(phone.DockerUrl))
            return BadRequest(new { error = "Container not running", dockerStatus = phone.DockerStatus });

        string? whatsappMessageId = null;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var response        = await client.PostAsJsonAsync($"{phone.DockerUrl}{endpoint}", request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                // ── חלץ messageId ──────────────────────────────────
                try
                {
                    var json = JsonSerializer.Deserialize<JsonElement>(responseContent);
                    if (json.TryGetProperty("messageId", out var msgId))
                        whatsappMessageId = msgId.GetString();
                }
                catch { }

                // ✅ רשום ל-sender_log בלבד — messages מנוהל ע"י webhook
                await _senderLogService.LogAsync(
                    phoneId, jid, messageType, logContent,
                    whatsappMessageId: whatsappMessageId);

                _logger.LogInformation(
                    "[SEND] ✓ type={Type} jid={Jid} msgId={MsgId}",
                    messageType, jid, whatsappMessageId);
            }
            else
            {
                // ✅ רשום כישלון ל-sender_log
                await _senderLogService.LogAsync(
                    phoneId, jid, messageType, logContent,
                    status: "failed",
                    errorMessage: $"{(int)response.StatusCode}: {responseContent}");

                _logger.LogWarning("[SEND] Failed: {Status}", response.StatusCode);
            }

            return StatusCode((int)response.StatusCode,
                JsonSerializer.Deserialize<object>(responseContent));
        }
        catch (Exception ex)
        {
            await _senderLogService.LogAsync(
                phoneId, jid, messageType, logContent,
                status: "failed", errorMessage: ex.Message);

            _logger.LogError(ex, "[SEND] Error for phone {PhoneId}", phoneId);
            return StatusCode(503, new { error = "Container unavailable", details = ex.Message });
        }
    }
}

// ── DTOs (ללא שינוי) ──────────────────────────────────────────────────────────
public class SendTextRequest           { public string Jid { get; set; } = ""; public string Text { get; set; } = ""; }
public class ButtonItem                { public string Id  { get; set; } = ""; public string Text { get; set; } = ""; }
public class SendButtonsRequest        { public string Jid { get; set; } = ""; public string Text { get; set; } = ""; public string? Footer { get; set; } public List<ButtonItem> Buttons { get; set; } = new(); }
public class ListRow                   { public string Id { get; set; } = ""; public string Title { get; set; } = ""; public string? Description { get; set; } }
public class ListSection               { public string Title { get; set; } = ""; public List<ListRow> Rows { get; set; } = new(); }
public class SendListRequest           { public string Jid { get; set; } = ""; public string Text { get; set; } = ""; public string? Title { get; set; } public string ButtonText { get; set; } = "בחר אפשרות"; public string? Footer { get; set; } public List<ListSection> Sections { get; set; } = new(); }
public class SendButtonResponseRequest { public string Jid { get; set; } = ""; public string ButtonId { get; set; } = ""; public string DisplayText { get; set; } = ""; }
public class SendListResponseRequest   { public string Jid { get; set; } = ""; public string RowId { get; set; } = ""; public string? Title { get; set; } }
public class SendPingRequest           { public string Jid { get; set; } = ""; public string? Text { get; set; } }
