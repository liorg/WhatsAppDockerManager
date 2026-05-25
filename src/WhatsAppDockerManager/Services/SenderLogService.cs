using System.Text.Json;
using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services;

public interface ISenderLogService
{
    Task<SenderLog> LogAsync(
        Guid phoneId,
        string jid,
        string messageType,
        object content,
        string? whatsappMessageId = null,
        string status = "sent",
        string? errorMessage = null);
}

public class SenderLogService : ISenderLogService
{
    // ✅ ISupabaseService (Singleton) — מוזרק ישירות, לא Client
    private readonly ISupabaseService _supabaseService;
    private readonly ILogger<SenderLogService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public SenderLogService(
        ISupabaseService supabaseService,   // ← Singleton, תואם
        ILogger<SenderLogService> logger)
    {
        _supabaseService = supabaseService;
        _logger          = logger;
    }

    public async Task<SenderLog> LogAsync(
        Guid phoneId,
        string jid,
        string messageType,
        object content,
        string? whatsappMessageId = null,
        string status = "sent",
        string? errorMessage = null)
    {
        // ── נסה למצוא contact_id לפי jid ─────────────────────────
        var contactNumber = jid.Contains('@') ? jid.Split('@')[0] : jid;
        var contact = await _supabaseService.GetContactByNumberAsync(phoneId, contactNumber);

        // ── סריאליזציה ────────────────────────────────────────────
        string contentJson;
        try
        {
            contentJson = content is string s ? s : JsonSerializer.Serialize(content, _jsonOptions);
        }
        catch
        {
            contentJson = "{}";
        }

        var log = new SenderLog
        {
            Id                = Guid.NewGuid(),
            PhoneId           = phoneId,
            ContactId         = contact?.Id,
            Jid               = jid,
            MessageType       = messageType,
            Content           = contentJson,
            WhatsappMessageId = whatsappMessageId,
            Status            = status,
            ErrorMessage      = errorMessage,
            SentAt            = DateTime.UtcNow,
            CreatedAt         = DateTime.UtcNow,
        };

        try
        {
            // ── שמירה דרך AddSenderLogAsync ───────────────────────
            return await _supabaseService.AddSenderLogAsync(log);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SENDER-LOG] Failed to insert");
            return log;
        }
    }
}