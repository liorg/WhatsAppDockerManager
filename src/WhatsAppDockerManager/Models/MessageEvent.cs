using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("message_events")]
public class MessageEvent : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("whatsapp_message_id")]
    public string WhatsappMessageId { get; set; } = string.Empty;

    [Column("phone_id")]
    public Guid? PhoneId { get; set; }

    [Column("jid")]
    public string? Jid { get; set; }

    [Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    [Column("status_code")]
    public int? StatusCode { get; set; }

    [Column("error_code")]
    public string? ErrorCode { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("raw_payload")]
    public string? RawPayload { get; set; }  // JSONB — Supabase C# client מקבל string/JSON

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }
}