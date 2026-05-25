using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

// ════════════════════════════════════════════════════════════════════
// Model — מיפוי לטבלת sender_log
// ════════════════════════════════════════════════════════════════════
[Table("sender_log")]
public class SenderLog : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("phone_id")]
    public Guid PhoneId { get; set; }

    [Column("contact_id")]
    public Guid? ContactId { get; set; }

    [Column("jid")]
    public string Jid { get; set; } = string.Empty;

    [Column("message_type")]
    public string MessageType { get; set; } = string.Empty;   // text/buttons/list/ping...

    [Column("content")]
    public string Content { get; set; } = "{}";               // JSON

    [Column("whatsapp_message_id")]
    public string? WhatsappMessageId { get; set; }

    [Column("status")]
    public string Status { get; set; } = "sent";              // sent / failed

    [Column("sent_at")]
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}