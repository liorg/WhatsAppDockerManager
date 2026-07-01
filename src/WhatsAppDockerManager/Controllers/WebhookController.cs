using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;

namespace WhatsAppDockerManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebhookController : ControllerBase
{
    private readonly IWebhookDispatcherService _dispatcher;
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
        ILogger<WebhookController> logger,
        IWebhookDispatcherService dispatcher
        )
    {
        _containerManager = containerManager;
        _supabaseService  = supabaseService;
        _logger           = logger;
        _dispatcher       = dispatcher;
    }

    [HttpPost("container-event/{phoneId}")]
    public async Task<IActionResult> ContainerEvent(Guid phoneId, [FromBody] ContainerEventPayload payload)
    {
         _logger.LogWarning("[RAW-PAYLOAD] {Json}",  System.Text.Json.JsonSerializer.Serialize(payload));
        _logger.LogInformation("RAW-PAYLOAD]  Container event for phone {PhoneId}: {Event}", phoneId, payload.Event ?? "unknown");

        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });


        // ← תיעוד גנרי — כל event עם MessageId נכתב אוטומטית ל-message_events
        if (!string.IsNullOrEmpty(payload.MessageId))
        {
            await _supabaseService.InsertMessageEventAsync(
                whatsappMessageId: payload.MessageId,
                phoneId:           phoneId,
                jid:               payload.Jid,
                eventType:         payload.Event ?? "unknown",
                statusCode:        payload.Status,
                errorCode:         payload.ErrorCode,
                errorMessage:      payload.ErrorMessage,
                rawPayload:        payload);
        }


        switch (payload.Event)
        {
            case "authenticated":
                await HandleAuthenticated(phoneId, phone, payload);
                break;
            case "disconnected":
                _logger.LogWarning("RAW-PAYLOAD] Phone {PhoneId} disconnected", phoneId);
                await _supabaseService.UpdatePhoneDockerStatusAsync(phoneId, PhoneDockerStatus.Error, errorMessage: "WhatsApp disconnected");
                break;
          case "pairing_code":                                                        // ← הוסף את כל הבלוק הזה
                _logger.LogInformation("[PAIRING] Phone {PhoneId} pairing code ready", phoneId);
                if (!string.IsNullOrEmpty(payload.PairingCode))
                    await _supabaseService.UpdatePhonePairingCodeAsync(phoneId, payload.PairingCode);
                await _supabaseService.UpdatePhoneDockerStatusAsync(phoneId, PhoneDockerStatus.Pending);
                break;
            case "qr":
                _logger.LogInformation("RAW-PAYLOAD] Phone {PhoneId} waiting for QR scan", phoneId);
                await _supabaseService.UpdatePhoneDockerStatusAsync(phoneId, PhoneDockerStatus.Pending);
                break;
                case "message_status":
                _logger.LogInformation("[STATUS] Phone {PhoneId} message {MessageId} status={Status}", 
                    phoneId, payload.MessageId, payload.Status);
                 await _supabaseService.UpdateMessageStatusAsync(payload.MessageId, payload.Status);
    break;
            case "message":
                await HandleIncomingMessage(phoneId, phone, payload);
                break;
            default:
                _logger.LogWarning("RAW-PAYLOAD] Unknown event type: {Event}", payload.Event);
                break;
        }

        return Ok(new { received = true });
    }
private static readonly ConcurrentDictionary<string, SemaphoreSlim> 
    _numberLocks = new();
    /// <summary>
    /// 
    /// </summary>
    /// <param name="phoneId"></param>
    /// <param name="phone"></param>
    /// <param name="payload"></param>
    /// <returns></returns>
    /// journalctl -u whatsapp-manager.service   -f --no-pager | grep "[AUTH]"
private async Task HandleAuthenticated(Guid phoneId, Phone phone, ContainerEventPayload payload)
{
    _logger.LogInformation("[AUTH] Phone {PhoneId} authenticated as {Phone}", phoneId, payload.Phone);

    if (string.IsNullOrEmpty(payload.Phone))
        return;
    if (payload.PayloadPhoneId.HasValue && payload.PayloadPhoneId.Value != phoneId)
    {
        _logger.LogWarning("[AUTH] PhoneId mismatch! URL={UrlId} Payload={PayloadId} — ignoring creds",
            phoneId, payload.PayloadPhoneId.Value);
        return;
    }
    var number = "+" + payload.Phone.Replace("+", "");
    _logger.LogInformation("[AUTH] Authenticated phone number: {Number}", number);

    // ── שמור creds תמיד — ללא קשר לתוצאת הtakeover ──────────────
    if (!string.IsNullOrEmpty(payload.CredsB64))
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(payload.CredsB64)
            )
        )[..12];
        _logger.LogInformation(
            "[AUTH] Got creds. PhoneId={PhoneId}, Len={Len}, Hash={Hash}",
            phoneId, payload.CredsB64.Length, hash);
        await _supabaseService.UpdatePhoneCredsAsync(phoneId, payload.CredsB64);
    }

    // ── semaphore לפי מספר — מונע race condition בין containers ──
    var sem = _numberLocks.GetOrAdd(number, _ => new SemaphoreSlim(1, 1));
    await sem.WaitAsync();

    try
    {
        var freshPhone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (freshPhone == null) return;

        // ── בדוק מי בעל הrevision הגבוה ביותר לפי מספר ────────────
        // הrevision נקבע ב-StartPhoneContainerAsync לפני הפעלת container
        var maxRevision = await _supabaseService.GetMaxRevisionByNumberAsync(number);

        if (freshPhone.AuthRevision < maxRevision)
        {
            // ── אני לא המנצח — revision שלי נמוך מהמקסימום ─────────
            _logger.LogWarning(
                "[AUTH] Not the winner. PhoneId={PhoneId} rev={Rev} < MaxRev={Max} — going inactive",
                phoneId, freshPhone.AuthRevision, maxRevision);

            await _supabaseService.SetPhoneStatusAsync(phoneId, "inactive");
            await _supabaseService.UpdatePhoneDockerStatusAsync(phoneId, PhoneDockerStatus.Stopped);
            return;
        }

        // ── אני המנצח — הפעל את עצמי ────────────────────────────────
        await _supabaseService.SetPhoneStatusAsync(phoneId, "active");
        await _supabaseService.UpdatePhoneDockerStatusAsync(phoneId, PhoneDockerStatus.Running);
        await _supabaseService.UpdatePhoneNumberAsync(phoneId, number);
        await _supabaseService.ClearPairingCodeAsync(phoneId);   // ← הוסף

        // ── השבת את כל שאר הphones עם אותו מספר ────────────────────
        var allSameNumber = await _supabaseService.GetPhonesByNumberAsync(number);
        foreach (var oldPhone in allSameNumber.Where(p => p.Id != phoneId && p.Status == "active"))
        {
            _logger.LogWarning(
                "[AUTH] Takeover: {OldId} (rev={OldRev}) → {NewId} (rev={NewRev})",
                oldPhone.Id, oldPhone.AuthRevision, phoneId, freshPhone.AuthRevision);

            await _supabaseService.SetPhoneStatusAsync(oldPhone.Id, "inactive");
            await _supabaseService.UpdatePhoneDockerStatusAsync(oldPhone.Id, PhoneDockerStatus.Stopped);
        }

        _logger.LogInformation(
            "[AUTH] ✓ Phone {PhoneId} active | number={Number} rev={Rev}",
            phoneId, number, freshPhone.AuthRevision);
    }
    finally
    {
        // ── שחרר semaphore תמיד — גם במקרה של exception ────────────
        sem.Release();
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
            string? contactName = null;
            string? rawLid      = null;

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

            // ── user_id מה-phone ─────────────────────────────────
            var userId = phone.UserId;

            // ── Resolve contactNumber + contactLid ────────────────
            string  contactNumber;
            string? contactLid;

            if (isLidJid)
            {
                contactLid    = jidLocal;
                contactNumber = jidLocal;

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
            // הודעה יוצאת (fromMe=true)
            // ══════════════════════════════════════════════════════
            if (!isIncoming)
            {
                var outContact = await _supabaseService.GetContactByNumberAsync(phoneId, contactNumber)
                              ?? await _supabaseService.GetContactByLidAsync(phoneId, contactNumber);

                if (outContact == null)
                {
                    _logger.LogInformation("[PING-OUT] No contact for {Number} — creating draft", contactNumber);
                    outContact = await _supabaseService.CreateDraftContactAsync(
                        phoneId, contactNumber, contactLid, contactName, userId);
                }

                _logger.LogInformation("[PING-OUT] Saved outgoing PING for contact {ContactId}", outContact.Id);
                await SaveMessage(phoneId, phone, outContact, contactNumber, contactLid, isIncoming: false, payload);
                return;
            }

            // ══════════════════════════════════════════════════════
            // הודעה נכנסת
            // ══════════════════════════════════════════════════════
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

                if (IsValidLid(contactLid) && !IsValidLid(contact.Lid))
                { contact.Lid = contactLid; needsUpdate = true; }

                if (userId.HasValue && contact.UserId == null)
                { contact.UserId = userId; needsUpdate = true; }

                if (needsUpdate)
                    await _supabaseService.UpdateContactAsync(contact);

                _logger.LogInformation("[MSG] Found existing contact {Id}", contact.Id);
            }
            else
            {
                contact = await _supabaseService.CreateDraftContactAsync(
                    phoneId, contactNumber, contactLid, contactName, userId);
                _logger.LogInformation("[MSG] Created draft contact {Id} lid={Lid}", contact.Id, contactLid);
            }

            _logger.LogInformation("[MSG] Draft contact {Id} waiting for user selection in wizard", contact.Id);
            await SaveMessage(phoneId, phone, contact, contactNumber, contactLid, isIncoming, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message for phone {PhoneId}", phoneId);
        }
    }

private async Task SaveMessage(
    Guid phoneId, Phone phone, Contact contact,
    string contactNumber, string? contactLid,
    bool isIncoming, ContainerEventPayload payload)
{
      _logger.LogWarning("[MSG-SAVE-DATA] Keys={Keys}", 
        payload.Data != null ? string.Join(",", payload.Data.Keys) : "null");
    var messageContent = new Dictionary<string, object?>();
 
    if (payload.Data != null)
    {
        TryAdd(payload.Data, messageContent, "text");
        TryAdd(payload.Data, messageContent, "caption");
        TryAdd(payload.Data, messageContent, "buttonId");
        TryAdd(payload.Data, messageContent, "selectedId");
        TryAdd(payload.Data, messageContent, "displayText");
        TryAdd(payload.Data, messageContent, "title");
        TryAdd(payload.Data, messageContent, "description");
        TryAdd(payload.Data, messageContent, "buttonText");
        TryAdd(payload.Data, messageContent, "footer");
        TryAdd(payload.Data, messageContent, "sections");
        TryAdd(payload.Data, messageContent, "buttons");


           // ← הוסף זמנית
    _logger.LogWarning("[MSG-SAVE] mediaUrl in Data={Exists} value={Val}",
        payload.Data.ContainsKey("mediaUrl"),
        payload.Data.TryGetValue("mediaUrl", out var v) ? v?.ToString() : "null");
        TryAdd(payload.Data, messageContent, "mediaUrl");    // ← הוסף
    }
 
    if (!string.IsNullOrEmpty(payload.Type))
        messageContent["type"] = payload.Type;
 
    // ── חלץ mediaUrl ──────────────────────────────────────────────
    string? mediaUrl = null;
    if (payload.Data?.TryGetValue("mediaUrl", out var mediaUrlVal) == true)
        mediaUrl = mediaUrlVal?.ToString();
 
    // ── המרת timestamp ────────────────────────────────────────────
    DateTime? whatsappTimestamp = null;
    if (payload.Timestamp != null)
    {
        long epochSeconds = 0;
        if (payload.Timestamp is System.Text.Json.JsonElement tsEl &&
            tsEl.ValueKind == System.Text.Json.JsonValueKind.Number)
            epochSeconds = tsEl.GetInt64();
        else
            long.TryParse(payload.Timestamp.ToString(), out epochSeconds);
 
        if (epochSeconds > 0)
            whatsappTimestamp = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
    }
 
    var messageSender = isIncoming
        ? (contactLid ?? contactNumber)
        : phone.Number ?? contactNumber;
 
    var savedMessage=await _supabaseService.AddMessageAsync(
        phoneId, contact.Id, messageSender, messageContent,
        direction:         isIncoming,
        leafId:            null,
        whatsappMessageId: payload.MessageId,
        whatsappTimestamp: whatsappTimestamp,
        mediaUrl:          mediaUrl);    
    
        // ── Dispatch ל-webhooks רשומים (background, לא חוסם) ─────────────────
    _ = Task.Run(async () =>
    {
        await _dispatcher.DispatchAsync(
            phoneId,
            contact.Id,
            savedMessage.Id,
            isIncoming);
    });

}



    // ── Helper ────────────────────────────────────────────────────────────────
    private static void TryAdd(
        Dictionary<string, object> source,
        Dictionary<string, object?> target,
        string key)
    {
        if (source.TryGetValue(key, out var value))
            target[key] = value;
    }
}

public class ContainerEventPayload
{
    [JsonPropertyName("event")]        public string? Event        { get; set; }
    [JsonPropertyName("messageId")]    public string? MessageId    { get; set; }
    [JsonPropertyName("jid")]          public string? Jid          { get; set; }
    [JsonPropertyName("type")]         public string? Type         { get; set; }
    [JsonPropertyName("data")]         public Dictionary<string, object>? Data { get; set; }
    [JsonPropertyName("timestamp")]    public object? Timestamp    { get; set; }
    [JsonPropertyName("phone")]        public string? Phone        { get; set; }
    [JsonPropertyName("name")]         public string? Name           { get; set; }
    [JsonPropertyName("creds_b64")]    public string? CredsB64       { get; set; }
    [JsonPropertyName("authRevision")] public int?    AuthRevision   { get; set; }
    [JsonPropertyName("phoneId")]      public Guid?   PayloadPhoneId { get; set; }
    [JsonPropertyName("userDisplay")]  public string? UserDisplay   { get; set; }
    [JsonPropertyName("pairingCode")]  public string? PairingCode   { get; set; }   
    [JsonPropertyName("status")]       public int?    Status         { get; set; }
    [JsonPropertyName("errorCode")]    public string? ErrorCode    { get; set; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
}
