using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;

namespace WhatsAppDockerManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebhookController : ControllerBase
{
    private readonly IContainerManager _containerManager;
    private readonly ISupabaseService _supabaseService;
    private readonly ILogger<WebhookController> _logger;

    private static readonly HashSet<string> _ignoredJidDomains = new(StringComparer.OrdinalIgnoreCase)
        { "broadcast", "g.us", "newsletter" };

    private static readonly HashSet<string> _ignoredJidLocals = new(StringComparer.OrdinalIgnoreCase)
        { "status", "0" };

    private static readonly HashSet<string> _bogusLidValues = new(StringComparer.OrdinalIgnoreCase)
        { "status", "broadcast", "0", "", "null", "undefined" };

    private static bool IsValidLid(string? lid) =>
        !string.IsNullOrWhiteSpace(lid) && !_bogusLidValues.Contains(lid);

    public WebhookController(
        IContainerManager containerManager,
        ISupabaseService supabaseService,
        ILogger<WebhookController> logger)
    {
        _containerManager = containerManager;
        _supabaseService  = supabaseService;
        _logger           = logger;
    }

    [HttpPost("container-event/{phoneId}")]
    public async Task<IActionResult> ContainerEvent(Guid phoneId, [FromBody] ContainerEventPayload payload)
    {
        _logger.LogInformation("Container event for phone {PhoneId}: {Event}", phoneId, payload.Event ?? "unknown");

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
                await HandleIncomingMessage(phoneId, phone, payload);
                break;
            default:
                _logger.LogWarning("Unknown event type: {Event}", payload.Event);
                break;
        }

        return Ok(new { received = true });
    }

    private async Task HandleAuthenticated(Guid phoneId, Phone phone, ContainerEventPayload payload)
    {
        _logger.LogInformation("Phone {PhoneId} authenticated as {Phone}", phoneId, payload.Phone);
        await _supabaseService.UpdatePhoneDockerStatusAsync(phoneId, PhoneDockerStatus.Running);

        if (!string.IsNullOrEmpty(payload.Phone))
            await _supabaseService.UpdatePhoneNumberAsync(phoneId, "+" + payload.Phone.Replace("+", ""));

        if (!string.IsNullOrEmpty(payload.CredsB64))
        {
            await _supabaseService.UpdatePhoneCredsAsync(phoneId, payload.CredsB64);
            _logger.LogInformation("Saved creds_base64 for phone {PhoneId}", phoneId);
        }
    }

    private async Task HandleIncomingMessage(Guid phoneId, Phone phone, ContainerEventPayload payload)
    {
        if (string.IsNullOrEmpty(payload.Jid))
        {
            _logger.LogWarning("Message without JID for phone {PhoneId}", phoneId);
            return;
        }

        try
        {
            _logger.LogInformation("[MSG-RAW] Jid={Jid} Type={Type} Data={Data}",
                payload.Jid, payload.Type,
                System.Text.Json.JsonSerializer.Serialize(payload.Data));

            // ── Parse JID ─────────────────────────────────────────
            var jidParts  = payload.Jid.Split('@');
            var jidLocal  = jidParts[0];
            var jidDomain = jidParts.Length > 1 ? jidParts[1] : "";
            bool isLidJid = jidDomain == "lid";

            // ── סנן broadcast/status/groups ───────────────────────
            if (_ignoredJidDomains.Contains(jidDomain) || _ignoredJidLocals.Contains(jidLocal))
            {
                _logger.LogInformation("[MSG] Ignored JID={Jid}", payload.Jid);
                return;
            }

            // ── מניעת כפילויות ────────────────────────────────────
            if (!string.IsNullOrEmpty(payload.MessageId))
            {
                var exists = await _supabaseService.MessageExistsAsync(payload.MessageId);
                if (exists)
                {
                    _logger.LogInformation("[MSG] Duplicate whatsapp_message_id={MsgId} — skipping", payload.MessageId);
                    return;
                }
            }

            // ── Extract payload data ───────────────────────────────
            string? contactName   = null;
            string? rawLid        = null;

            if (payload.Data != null)
            {
                if (payload.Data.TryGetValue("pushName", out var pushName))
                    contactName = pushName?.ToString();

                if (payload.Data.TryGetValue("lid", out var lidVal))
                {
                    var rawLidRaw = lidVal?.ToString();
                    var lidLocal  = rawLidRaw?.Split('@')[0];
                    rawLid = IsValidLid(lidLocal) ? rawLidRaw : null;
                }
            }

            // ── fromMe ────────────────────────────────────────────
            bool isIncoming = true;
            if (payload.Data?.TryGetValue("fromMe", out var fromMe) == true)
            {
                if (fromMe is System.Text.Json.JsonElement je)
                    isIncoming = !je.GetBoolean();
                else
                    isIncoming = !Convert.ToBoolean(fromMe);
            }

            // ── Resolve contactNumber + contactLid ────────────────
            string  contactNumber;
            string? contactLid;

            if (isLidJid)
            {
                contactLid    = jidLocal;
                contactNumber = jidLocal; // זמני — יוחלף אם נמצא contact קיים

                var byLid = await _supabaseService.GetContactByLidAsync(phoneId, jidLocal);
                if (byLid != null)
                    contactNumber = byLid.Number;
            }
            else
            {
                contactNumber = jidLocal;
                var rawLidLocal = string.IsNullOrEmpty(rawLid) ? null : rawLid.Split('@')[0];
                contactLid = IsValidLid(rawLidLocal) ? rawLidLocal : null;
            }

            // ══════════════════════════════════════════════════════
            // הודעה יוצאת (fromMe=true) — זוהי הודעת ה-PING שלנו
            // ══════════════════════════════════════════════════════
            if (!isIncoming)
            {
                // מצא/צור contact לפי מספר היעד
                var outContact = await _supabaseService.GetContactByNumberAsync(phoneId, contactNumber)
                              ?? await _supabaseService.GetContactByLidAsync(phoneId, contactNumber);

                if (outContact == null)
                {
                    _logger.LogInformation("[PING-OUT] No contact for {Number} — creating draft", contactNumber);
                    outContact = await _supabaseService.CreateDraftContactAsync(phoneId, contactNumber, contactLid, contactName);
                }

                // ── קשר contact ← ping_sender ─────────────────────
                // ping_sender נוצר ע"י SendController — ייתכן race condition
                // עם ה-webhook. נסה כמה פעמים עם delay קצר.
                PingSender? ps = null;
                for (int attempt = 0; attempt < 5 && ps == null; attempt++)
                {
                    if (attempt > 0)
                        await Task.Delay(500); // המתן ל-ping_sender להיכתב ל-DB

                    if (!string.IsNullOrEmpty(payload.MessageId))
                        ps = await _supabaseService.GetPingSenderByMessageIdAsync(phoneId, payload.MessageId);

                    ps ??= await _supabaseService.GetPendingPingSenderAsync(phoneId, contactNumber);
                }

                if (ps != null && ps.ContactId == null)
                {
                    ps.ContactId = outContact.Id;
                    await _supabaseService.UpdatePingSenderAsync(ps);
                    _logger.LogInformation("[PING-OUT] Linked ping_sender {PsId} → contact {ContactId}",
                        ps.Id, outContact.Id);
                }
                else if (ps == null)
                {
                    _logger.LogWarning("[PING-OUT] ping_sender not found for messageId={MsgId} number={Number} — will retry on next message",
                        payload.MessageId, contactNumber);
                }

                await SaveMessage(phoneId, phone, outContact, contactNumber, contactLid, isIncoming: false, payload);
                return;
            }

            // ══════════════════════════════════════════════════════
            // הודעה נכנסת — מהלקוח (תגובה על ה-PING)
            // ══════════════════════════════════════════════════════

            // חפש contact קיים
            Contact? existing = null;
            if (!string.IsNullOrEmpty(contactLid))
                existing = await _supabaseService.GetContactByLidAsync(phoneId, contactLid);
            if (existing == null)
                existing = await _supabaseService.GetContactByNumberAsync(phoneId, contactNumber);

            Contact contact;
            if (existing != null)
            {
                contact = existing;
                bool needsUpdate = false;

                if (!string.IsNullOrEmpty(contactName))
                {
                    if (string.IsNullOrEmpty(contact.WhatsappName) || contact.WhatsappName != contactName)
                    { contact.WhatsappName = contactName; needsUpdate = true; }
                    if (string.IsNullOrEmpty(contact.Name) || contact.Name == contact.Number || contact.Name == contact.Lid)
                    { contact.Name = contactName; needsUpdate = true; }
                }

                // עדכן LID רק אם ריק או מזויף
                if (IsValidLid(contactLid) && !IsValidLid(contact.Lid))
                { contact.Lid = contactLid; needsUpdate = true; }

                if (needsUpdate)
                    await _supabaseService.UpdateContactAsync(contact);

                _logger.LogInformation("[MSG] Found existing contact {Id}", contact.Id);
            }
            else
            {
                // צור draft חדש
                contact = await _supabaseService.CreateDraftContactAsync(
                    phoneId, contactNumber, contactLid, contactName);
                _logger.LogInformation("[MSG] Created draft contact {Id} lid={Lid}", contact.Id, contactLid);
            }

            // ── אין auto-match — המשתמש בוחר בשלב 2 ──────────────
            // ping_sender.target_number = מספר טלפון (972...)
            // הודעה נכנסת עם LID (46...) — אין קשר ישיר ידוע
            // הקישור קורה ב-select_response (שלב 3) על ידי המשתמש
            _logger.LogInformation("[MSG] Draft contact {Id} waiting for user selection in wizard", contact.Id);

            await SaveMessage(phoneId, phone, contact, contactNumber, contactLid, isIncoming, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message for phone {PhoneId}", phoneId);
        }
    }

    private async Task SaveMessage(Guid phoneId, Phone phone, Contact contact,
        string contactNumber, string? contactLid, bool isIncoming, ContainerEventPayload payload)
    {
        var messageContent = new Dictionary<string, object?>();
        if (payload.Data != null)
        {
            if (payload.Data.TryGetValue("text",       out var text))       messageContent["text"]       = text;
            if (payload.Data.TryGetValue("type",       out var type))       messageContent["type"]       = type;
            if (payload.Data.TryGetValue("buttonId",   out var buttonId))   messageContent["buttonId"]   = buttonId;
            if (payload.Data.TryGetValue("selectedId", out var selectedId)) messageContent["selectedId"] = selectedId;
            if (payload.Data.TryGetValue("caption",    out var caption))    messageContent["caption"]    = caption;
        }
        if (!messageContent.ContainsKey("type") && !string.IsNullOrEmpty(payload.Type))
            messageContent["type"] = payload.Type;

        var messageSender = isIncoming
            ? (contactLid ?? contactNumber)
            : phone.Number ?? contactNumber;

        var message = await _supabaseService.AddMessageAsync(
            phoneId, contact.Id, messageSender, messageContent,
            direction: isIncoming, leafId: null,
            whatsappMessageId: payload.MessageId);

        _logger.LogInformation("[MSG] Saved {Dir} message {MsgId} phone={PhoneId}",
            isIncoming ? "incoming" : "outgoing", message.Id, phoneId);
    }
}

public class ContainerEventPayload
{
    [JsonPropertyName("event")]     public string? Event     { get; set; }
    [JsonPropertyName("messageId")] public string? MessageId { get; set; }
    [JsonPropertyName("jid")]       public string? Jid       { get; set; }
    [JsonPropertyName("type")]      public string? Type      { get; set; }
    [JsonPropertyName("data")]      public Dictionary<string, object>? Data { get; set; }
    [JsonPropertyName("timestamp")] public object? Timestamp { get; set; }
    [JsonPropertyName("phone")]     public string? Phone     { get; set; }
    [JsonPropertyName("name")]      public string? Name      { get; set; }
    [JsonPropertyName("creds_b64")] public string? CredsB64  { get; set; }
}