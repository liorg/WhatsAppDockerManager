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
        {
            var normalizedPhone = "+" + payload.Phone.Replace("+", "");
            await _supabaseService.UpdatePhoneNumberAsync(phoneId, normalizedPhone);
        }

        if (!string.IsNullOrEmpty(payload.CredsB64))
        {
            await _supabaseService.UpdatePhoneCredsAsync(phoneId, payload.CredsB64);
            _logger.LogInformation("Saved creds_base64 for phone {PhoneId}", phoneId);
        }
    }

    /// <summary>
    /// Handle incoming message.
    /// 
    /// עיקרון: שמור את כל ההודעות תמיד — גם אם לא מכירים את השולח.
    /// המשתמש יזהה ויקשר בשלב 2 של הוויזארד.
    /// 
    /// JID formats:
    ///   "972504476645@s.whatsapp.net" → number JID
    ///   "46037871886515@lid"          → LID JID (WhatsApp חדש)
    /// </summary>
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

            // ── מניעת כפילויות — בדוק אם ההודעה כבר נשמרה ──────────────
            if (!string.IsNullOrEmpty(payload.MessageId))
            {
                var exists = await _supabaseService.MessageExistsAsync(payload.MessageId);
                if (exists)
                {
                    // אם יש pushName בהודעה הכפולה — עדכן את שם ה-contact
                    if (payload.Data?.TryGetValue("pushName", out var dupPushName) == true
                        && dupPushName is System.Text.Json.JsonElement dupNameEl
                        && dupNameEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var dupName = dupNameEl.GetString();
                        if (!string.IsNullOrEmpty(dupName))
                        {
                            var jidForDup = payload.Jid?.Split('@')[0];
                            if (!string.IsNullOrEmpty(jidForDup))
                            {
                                // חפש contact לפי LID או מספר ועדכן שם
                                var contactForName = await _supabaseService.GetContactByLidAsync(phoneId, jidForDup)
                                    ?? await _supabaseService.GetContactByNumberAsync(phoneId, jidForDup);
                                if (contactForName != null && (string.IsNullOrEmpty(contactForName.Name) || contactForName.Name == contactForName.Number))
                                {
                                    contactForName.Name = dupName;
                                    await _supabaseService.UpdateContactAsync(contactForName);
                                    _logger.LogInformation("[MSG] Updated contact name from duplicate: {Name}", dupName);
                                }
                            }
                        }
                    }
                    _logger.LogInformation("[MSG] Duplicate whatsapp_message_id={MsgId} — skipping", payload.MessageId);
                    return;
                }
            }

            // ── Parse JID ─────────────────────────────────────────────────
            var jidParts  = payload.Jid.Split('@');
            var jidLocal  = jidParts[0];
            var jidDomain = jidParts.Length > 1 ? jidParts[1] : "";
            bool isLidJid = jidDomain == "lid";

            // ── Extract from payload.Data ──────────────────────────────────
            string? contactName   = null;
            string? payloadNumber = null;
            string? rawLid        = null;

            if (payload.Data != null)
            {
                if (payload.Data.TryGetValue("pushName", out var pushName))
                    contactName = pushName?.ToString();
                if (payload.Data.TryGetValue("number", out var num))
                    payloadNumber = num?.ToString()?.TrimStart('+');
                if (payload.Data.TryGetValue("lid", out var lidVal))
                    rawLid = lidVal?.ToString(); // e.g. "46037871886515@lid"
            }

            // ── fromMe ────────────────────────────────────────────────────
            bool isIncoming = true;
            if (payload.Data?.TryGetValue("fromMe", out var fromMe) == true)
            {
                if (fromMe is System.Text.Json.JsonElement je)
                    isIncoming = !je.GetBoolean();
                else
                    isIncoming = !Convert.ToBoolean(fromMe);
            }

            // ── Resolve contactNumber + contactLid ────────────────────────
            string  contactNumber;
            string? contactLid;

            if (isLidJid)
            {
                contactLid = jidLocal; // ה-LID ללא @lid

                // הודעה יוצאת עם LID (fromMe=true) — זה PING שלנו
                // לא יוצרים contact חדש, שומרים רק אם ה-contact כבר קיים
                if (!isIncoming)
                {
                    var existingByLid = await _supabaseService.GetContactByLidAsync(phoneId, jidLocal);
                    if (existingByLid == null)
                    {
                        _logger.LogInformation("[MSG] LID-JID outgoing — no existing contact, skipping");
                        return;
                    }
                    _logger.LogInformation("[MSG] LID-JID outgoing — saving to existing contact {Id}", existingByLid.Id);
                    await SaveMessage(phoneId, phone, existingByLid, existingByLid.Number, contactLid, isIncoming: false, payload);
                    return;
                }

                // הודעה נכנסת — חפש contact קיים לפי LID
                var byLid = await _supabaseService.GetContactByLidAsync(phoneId, jidLocal);
                if (byLid != null)
                {
                    contactNumber = byLid.Number;
                    _logger.LogInformation("[MSG] Found by LID: contact={Id} number={Number}", byLid.Id, contactNumber);
                }
                else if (!string.IsNullOrEmpty(payloadNumber))
                {
                    // יש מספר ב-data
                    contactNumber = payloadNumber;
                    _logger.LogInformation("[MSG] LID-JID using payloadNumber={Number}", contactNumber);
                }
                else
                {
                    // חפש ping_sender פתוח לפי phone_id
                    var pendingPing = await _supabaseService.GetLatestPendingPingSenderAsync(phoneId);
                    if (pendingPing != null && !string.IsNullOrEmpty(pendingPing.TargetNumber))
                    {
                        contactNumber = pendingPing.TargetNumber;
                        _logger.LogInformation("[MSG] LID-JID matched via ping_sender: number={Number} lid={Lid}", contactNumber, jidLocal);
                    }
                    else
                    {
                        // אין ping_sender — זה מישהו אחר שפנה אלינו, צור draft contact
                        contactNumber = jidLocal; // LID כ-number זמני
                        _logger.LogInformation("[MSG] LID-JID incoming — no ping_sender, creating draft with LID as number");
                    }
                }
            }
            else
            {
                contactNumber = jidLocal;
                contactLid    = rawLid?.Split('@')[0];
            }

            // ── הודעה יוצאת (fromMe=true) — הודעת PING שלנו ─────────────
            if (!isIncoming)
            {
                var existingOut = await _supabaseService.GetContactByNumberAsync(phoneId, contactNumber);
                if (existingOut == null)
                {
                    // contact טרם נוצר — צור draft כדי לשמור את ה-PING
                    _logger.LogInformation("[MSG] Outgoing PING for new contact {Number} — creating draft", contactNumber);
                    existingOut = await _supabaseService.CreateDraftContactAsync(phoneId, contactNumber, contactLid, contactName);
                }

                // שמור הודעה יוצאת (direction=false)
                await SaveMessage(phoneId, phone, existingOut, contactNumber, contactLid, isIncoming: false, payload);
                return;
            }

            // ── הודעה נכנסת — שמור תמיד ──────────────────────────────────
            // חפש contact קיים (לפי LID או number)
            Contact? existing = null;
            if (!string.IsNullOrEmpty(contactLid))
                existing = await _supabaseService.GetContactByLidAsync(phoneId, contactLid);
            if (existing == null)
                existing = await _supabaseService.GetContactByNumberAsync(phoneId, contactNumber);

            Contact contact;
            if (existing != null)
            {
                contact = existing;
                // עדכן LID אם חסר
                // לא מעדכנים LID אוטומטית — זה יקרה רק בשלב 3 ע"י המשתמש
                // if (string.IsNullOrEmpty(existing.Lid) && !string.IsNullOrEmpty(contactLid))
                // {
                //     await _supabaseService.UpsertContactAsync(phoneId, contactNumber, name: contactName, lid: contactLid);
                //     contact.Lid = contactLid;
                // }
                _logger.LogInformation("[MSG] Found existing contact {Id} ({Number})", contact.Id, contactNumber);
            }
            else
            {
                // Contact חדש — צור עם tag=draft
                // draft = ממתין לזיהוי ע"י המשתמש בוויזארד
                contact = await _supabaseService.CreateDraftContactAsync(
                    phoneId, contactNumber, contactLid, contactName);
                _logger.LogInformation("[MSG] Created draft contact {Id} ({Number}) lid={Lid}",
                    contact.Id, contactNumber, contactLid);
            }

            // ── Match PingSender ───────────────────────────────────────────
            if (!string.IsNullOrEmpty(contactLid))
                await _supabaseService.MatchPingSenderByLidAsync(phoneId, contactLid, contact.Id);

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

        // sender = LID של הלקוח (זה מה ש-select_response ישתמש בו)
        var messageSender = isIncoming
            ? (contactLid ?? contactNumber)
            : phone.Number ?? contactNumber;

        var message = await _supabaseService.AddMessageAsync(
            phoneId, contact.Id, messageSender, messageContent,
            direction: isIncoming, leafId: null,
            whatsappMessageId: payload.MessageId);

        _logger.LogInformation("[MSG] Saved message {MsgId} from {Sender} (incoming={Inc}) phone={PhoneId}",
            message.Id, messageSender, isIncoming, phoneId);
    }
}

public class ContainerEventPayload
{
    [JsonPropertyName("event")]    public string? Event     { get; set; }
    [JsonPropertyName("messageId")] public string? MessageId { get; set; }
    [JsonPropertyName("jid")]      public string? Jid       { get; set; }
    [JsonPropertyName("type")]     public string? Type      { get; set; }
    [JsonPropertyName("data")]     public Dictionary<string, object>? Data { get; set; }
    [JsonPropertyName("timestamp")] public object? Timestamp { get; set; }
    [JsonPropertyName("phone")]    public string? Phone     { get; set; }
    [JsonPropertyName("name")]     public string? Name      { get; set; }
    [JsonPropertyName("creds_b64")] public string? CredsB64  { get; set; }
}