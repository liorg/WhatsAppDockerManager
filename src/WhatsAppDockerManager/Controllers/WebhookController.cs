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
    /// Handle incoming message - create contact if not exists and save message.
    /// 
    /// JID formats from WhatsApp:
    ///   - "972504476645@s.whatsapp.net"  → regular number JID
    ///   - "12345678901234567@lid"         → LID JID (new WhatsApp accounts)
    ///
    /// When JID is @lid format, we must look up the contact by LID, not by number.
    /// The number arrives separately in payload.Data["number"] or payload.Data["verifiedBizName"].
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
            _logger.LogInformation("[MSG-RAW] Jid={Jid} Type={Type} Data={Data}",
                payload.Jid,
                payload.Type,
                System.Text.Json.JsonSerializer.Serialize(payload.Data));

            // ── Parse JID ────────────────────────────────────────────────────
            var jidParts   = payload.Jid.Split('@');
            var jidLocal   = jidParts[0];                              // number OR lid-value
            var jidDomain  = jidParts.Length > 1 ? jidParts[1] : ""; // "s.whatsapp.net" or "lid"

            bool isLidJid  = jidDomain == "lid";

            // ── Extract name, number, lid from payload.Data ───────────────────
            string? contactName   = null;
            string? payloadNumber = null; // מספר טלפון אמיתי אם קיים ב-data

            if (payload.Data != null)
            {
                if (payload.Data.TryGetValue("pushName", out var pushName))
                    contactName = pushName?.ToString();
                if (payload.Data.TryGetValue("number", out var num))
                    payloadNumber = num?.ToString()?.TrimStart('+');
            }

            // ── Determine contactNumber and contactLid ────────────────────────
            // כלל: contactNumber = מספר טלפון בלבד (ספרות בלי @)
            //       contactLid    = ה-LID האמיתי של הלקוח (מה-JID כש@lid, או מה-sender)
            string  contactNumber;
            string? contactLid;

            // ── fromMe ────────────────────────────────────────────────────────
            bool isIncoming = true;
            if (payload.Data?.TryGetValue("fromMe", out var fromMe) == true)
            {
                if (fromMe is System.Text.Json.JsonElement jsonElement)
                    isIncoming = !jsonElement.GetBoolean();
                else
                    isIncoming = !Convert.ToBoolean(fromMe);
            }

            // ── LID מה-data (כולל @lid suffix) ───────────────────────────────
            string? rawLid = null;
            if (payload.Data?.TryGetValue("lid", out var lidVal) == true)
                rawLid = lidVal?.ToString(); // "46037871886515@lid"

            if (isLidJid)
            {
                // JID הוא LID — ה-number לא מגיע בכלל מ-WhatsApp
                contactLid = jidLocal; // ה-LID ללא @lid

                // חפש contact לפי LID תחת ה-phone הנוכחי
                var existingByLid = await _supabaseService.GetContactByLidAsync(phoneId, jidLocal);

                if (existingByLid != null)
                {
                    // מצאנו תחת ה-phone הנוכחי
                    contactNumber = existingByLid.Number;
                    _logger.LogInformation("[MSG] LID-JID: matched contact {ContactId} number={Number} on phone {PhoneId}",
                        existingByLid.Id, contactNumber, phoneId);
                }
                else
                {
                    // לא נמצא תחת ה-phone הנוכחי —
                    // ייתכן שה-contact נוצר תחת phone אחר (כשיש כמה containers לאותו לקוח)
                    // במקרה זה: שמור רק הודעה, אל תיצור contact כפול
                    _logger.LogWarning("[MSG] LID-JID {Lid}: no contact found under phone {PhoneId} — message will be saved to existing contact if found globally, otherwise skipped",
                        jidLocal, phoneId);

                    if (!string.IsNullOrEmpty(payloadNumber))
                    {
                        contactNumber = payloadNumber;
                        _logger.LogInformation("[MSG] LID-JID: using payloadNumber={Number}", contactNumber);
                    }
                    else
                    {
                        // אין מספר בכלל — דלג, אל תיצור contact עם LID כ-number
                        _logger.LogWarning("[MSG] LID-JID {Lid}: skipping — no number available", jidLocal);
                        return;
                    }
                }
            }
            else
            {
                // JID רגיל — המספר הוא jidLocal
                contactNumber = jidLocal;
                // LID מגיע מה-data["lid"] או מה-sender
                contactLid = rawLid?.Split('@')[0]; // נקה @lid suffix
            }

            // ── אל תיצור contact חדש אם זו הודעה יוצאת (fromMe=true) ──────
            Contact contact;
            if (!isIncoming)
            {
                var existingOut = await _supabaseService.GetContactByNumberAsync(phoneId, contactNumber);
                if (existingOut == null)
                {
                    _logger.LogWarning("[MSG] Outgoing for unknown contact {Number} — skipping", contactNumber);
                    return;
                }
                contact = existingOut;
            }
            else
            {
                // הודעה נכנסת
                // בדוק קודם אם contact קיים לפי LID (מונע כפילות בין phones)
                Contact? existingForMsg = null;
                if (!string.IsNullOrEmpty(contactLid))
                    existingForMsg = await _supabaseService.GetContactByLidAsync(phoneId, contactLid);
                if (existingForMsg == null)
                    existingForMsg = await _supabaseService.GetContactByNumberAsync(phoneId, contactNumber);

                if (existingForMsg != null)
                {
                    // contact קיים — עדכן שם אם צריך, אל תיצור
                    contact = existingForMsg;
                    _logger.LogInformation("[MSG] Found existing contact {ContactId} ({Number})", contact.Id, contactNumber);
                }
                else
                {
                    // contact חדש לגמרי — צור (אורגני, לא דרך PING)
                    contact = await _supabaseService.UpsertContactAsync(
                        phoneId,
                        contactNumber,
                        name: contactName,
                        lid: contactLid
                    );
                    _logger.LogInformation("[MSG] Created new contact {ContactId} ({Number}) lid={Lid}",
                        contact.Id, contactNumber, contactLid);
                }
            }

            // ── Match PingSender אם יש LID ────────────────────────────────────
            if (!string.IsNullOrEmpty(contactLid))
            {
                await _supabaseService.MatchPingSenderByLidAsync(phoneId, contactLid, contact.Id);
            }

            // ── Build message content ─────────────────────────────────────────
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

            // ── Sender field בהודעה: LID אם יש, אחרת number ─────────────────
            // זה מה שה-Python select_response ישתמש בו כ-LID
            var messageSender = isIncoming
                ? (contactLid ?? contactNumber)   // ← LID של הלקוח, או number כ-fallback
                : phone.Number ?? contactNumber;  // ← הטלפון שלנו

            var message = await _supabaseService.AddMessageAsync(
                phoneId,
                contact.Id,
                messageSender,
                messageContent,
                direction: isIncoming,
                leafId: null,
                whatsappMessageId: payload.MessageId
            );

            _logger.LogInformation("[MSG] Saved message {MessageId} from {Sender} (incoming={IsIncoming}) for phone {PhoneId}",
                message.Id, messageSender, isIncoming, phoneId);
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
