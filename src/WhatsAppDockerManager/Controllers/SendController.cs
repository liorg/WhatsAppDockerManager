using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhatsAppDockerManager.Controllers;

[ApiController]
[Route("api/phones/{phoneId}/send")]
public class SendController : ControllerBase
{
    private readonly ISupabaseService   _supabaseService;
    private readonly ISenderLogService  _senderLogService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SendController> _logger;

    public SendController(
        ISupabaseService   supabaseService,
        ISenderLogService  senderLogService,
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

    // ── Send template ─────────────────────────────────────────────────────────
    // המקום היחיד בשרשרת שמחולל את טקסט ההודעה.
    // ה-Worker וה-Spine מעבירים שם תבנית + פרמטרים as-is.
    [HttpPost("template")]
    public async Task<IActionResult> SendTemplate(Guid phoneId, [FromBody] SendTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Jid))
            return BadRequest(new { error = "jid is required" });
        if (string.IsNullOrWhiteSpace(request.Name) && !request.TemplateId.HasValue)
            return BadRequest(new { error = "name or templateId is required" });

        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });
        if (string.IsNullOrEmpty(phone.DockerUrl))
            return BadRequest(new { error = "Container not running", dockerStatus = phone.DockerStatus });

        var provider = string.IsNullOrWhiteSpace(phone.Provider) ? "baileys" : phone.Provider;

        // מסלול Cloud API עדיין לא קיים — נכשל מפורשות ולא בשקט.
        if (provider != "baileys")
            return StatusCode(501, new { error = "Template send is implemented for baileys only", provider });

        // ── שליפת התבנית ──────────────────────────────────────────────────────
        var lang = string.IsNullOrWhiteSpace(request.Lang) ? phone.Lang : request.Lang;

        var template = request.TemplateId.HasValue
            ? await _supabaseService.GetTemplateByIdAsync(phoneId, request.TemplateId.Value)
            : await _supabaseService.GetTemplateAsync(phoneId, request.Name, lang);

        if (template == null)
            return NotFound(new { error = "Template not found", name = request.Name, lang });

        if (template.Status != "approved")
            return Conflict(new { error = "Template is not approved", status = template.Status });
        if (!template.IsPublished)
            return Conflict(new { error = "Template is not published", name = template.Name });

        var content = template.Content;
        if (content?.Body == null || string.IsNullOrWhiteSpace(content.Body.Text))
            return BadRequest(new { error = "Template has no BODY" });

        // ── פרמטרים ───────────────────────────────────────────────────────────
        // params מובנה { header:[], body:[] }, או bodyParams שטוח כשאין HEADER.
        var pars = request.Params ?? new Dictionary<string, List<string>>();
        if (request.BodyParams is { Count: > 0 } && !pars.ContainsKey("body"))
            pars["body"] = request.BodyParams;

        var headerVals = pars.TryGetValue("header", out var hv) ? hv : new List<string>();
        var bodyVals   = pars.TryGetValue("body",   out var bv) ? bv : new List<string>();

        var headerFmt = content.Header?.Format ?? "none";

        if (headerFmt is "image" or "video" or "document")
        {
            return BadRequest(new
            {
                error  = "Media header is not supported on baileys template send",
                format = headerFmt,
            });
        }

        var headerText = headerFmt == "text" ? (content.Header?.Text ?? "") : "";

        var needHeader = MaxParamIndex(headerText);
        var needBody   = MaxParamIndex(content.Body.Text);

        if (headerVals.Count < needHeader || bodyVals.Count < needBody)
        {
            return BadRequest(new
            {
                error    = "Missing template parameters",
                required = new { header = needHeader, body = needBody },
                supplied = new { header = headerVals.Count, body = bodyVals.Count },
            });
        }

        // ── רינדור ────────────────────────────────────────────────────────────
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(headerText))
            lines.Add(FillParams(headerText, headerVals));

        lines.Add(FillParams(content.Body.Text, bodyVals));

        var footer   = content.Footer?.Text;
        var bodyOnly = string.Join("\n", lines);
        var fullText = string.IsNullOrWhiteSpace(footer) ? bodyOnly : $"{bodyOnly}\n{footer}";

        _logger.LogInformation(
            "[TEMPLATE] {Name}/{Lang} -> {Jid} | params h={H} b={B} len={Len}",
            template.Name, template.Lang, request.Jid,
            headerVals.Count, bodyVals.Count, fullText.Length);

        // ── שליחה לקונטיינר דרך ForwardToContainer ────────────────────────────
        // כך מקבלים sender_log, חילוץ messageId וטיפול שגיאות בחינם.
        // תבנית עם quick_reply → /send/buttons, אחרת הכפתורים נעלמים.
        var buttons = (content.Buttons ?? new List<TemplateButton>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .Select((x, i) => new ButtonItem { Id = $"btn_{i + 1}", Text = x.Text! })
            .ToList();

        if (buttons.Count > 0)
        {
            var btnReq = new SendButtonsRequest
            {
                Jid     = request.Jid,
                Text    = bodyOnly,
                Footer  = footer,
                Buttons = buttons,
            };

            return await ForwardToContainer(phoneId, "/send/buttons", btnReq, request.Jid,
                "template",
                new { template = template.Name, lang = template.Lang, text = bodyOnly, footer, buttons });
        }

        var txtReq = new SendTextRequest { Jid = request.Jid, Text = fullText };

        return await ForwardToContainer(phoneId, "/send/text", txtReq, request.Jid,
            "template",
            new { template = template.Name, lang = template.Lang, text = fullText });
    }

    // ── עזרי תבנית ────────────────────────────────────────────────────────────

    private static readonly Regex ParamRe =
        new(@"\{\{\s*(\d+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>המספר הגבוה ביותר שמופיע כ-{{n}} — כמה ערכים נדרשים לרכיב.</summary>
    private static int MaxParamIndex(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var max = 0;
        foreach (Match m in ParamRe.Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out var n) && n > max)
                max = n;
        }

        return max;
    }

    private static string FillParams(string? text, List<string> values)
    {
        return ParamRe.Replace(text ?? "", m =>
        {
            var idx = int.Parse(m.Groups[1].Value) - 1;
            return idx >= 0 && idx < values.Count ? values[idx] ?? "" : "";
        });
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


// ── DTOs ──────────────────────────────────────────────────────────────────────
public class SendTextRequest           { public string Jid { get; set; } = ""; public string Text { get; set; } = ""; }
public class ButtonItem                { public string Id  { get; set; } = ""; public string Text { get; set; } = ""; }
public class SendButtonsRequest        { public string Jid { get; set; } = ""; public string Text { get; set; } = ""; public string? Footer { get; set; } public List<ButtonItem> Buttons { get; set; } = new(); }
public class ListRow                   { public string Id { get; set; } = ""; public string Title { get; set; } = ""; public string? Description { get; set; } }
public class ListSection               { public string Title { get; set; } = ""; public List<ListRow> Rows { get; set; } = new(); }
public class SendListRequest           { public string Jid { get; set; } = ""; public string Text { get; set; } = ""; public string? Title { get; set; } public string ButtonText { get; set; } = "בחר אפשרות"; public string? Footer { get; set; } public List<ListSection> Sections { get; set; } = new(); }
public class SendButtonResponseRequest { public string Jid { get; set; } = ""; public string ButtonId { get; set; } = ""; public string DisplayText { get; set; } = ""; }
public class SendListResponseRequest   { public string Jid { get; set; } = ""; public string RowId { get; set; } = ""; public string? Title { get; set; } }
public class SendPingRequest           { public string Jid { get; set; } = ""; public string? Text { get; set; } }

public class SendImageRequest
{
    public string Jid { get; set; } = "";
    public string Image { get; set; } = "";
    public string? Caption { get; set; }
    public string? Mimetype { get; set; } = "image/jpeg";
}

public class SendStickerRequest
{
    public string Jid { get; set; } = "";
    public string Sticker { get; set; } = "";
}

public class SendTemplateRequest
{
    public string Jid { get; set; } = "";

    /// <summary>שם התבנית, כמו ב-Cloud API.</summary>
    public string Name { get; set; } = "";

    /// <summary>קוד שפה. אם ריק — נלקח מ-phones.lang.</summary>
    public string? Lang { get; set; }

    /// <summary>עוקף את name+lang כשידוע ה-id המדויק.</summary>
    public Guid? TemplateId { get; set; }

    /// <summary>{ "header": ["דני"], "body": ["בדיקה", "09:30"] } — לפי סדר {{1}},{{2}}.</summary>
    public Dictionary<string, List<string>>? Params { get; set; }

    /// <summary>חלופה שטוחה — ממופה ל-body.</summary>
    public List<string>? BodyParams { get; set; }
}
