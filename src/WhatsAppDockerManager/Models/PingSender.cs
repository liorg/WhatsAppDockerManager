using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("ping_sender")]
public class PingSender : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("phone_id")]
    public Guid PhoneId { get; set; }

    [Column("target_number")]
    public string TargetNumber { get; set; } = string.Empty;

    [Column("contact_id")]
    public Guid? ContactId { get; set; }

    [Column("lid")]
    public string? Lid { get; set; }

    [Column("ping_message_id")]
    public string? PingMessageId { get; set; }

    [Column("status")]
    public string Status { get; set; } = "pending";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("matched_at")]
    public DateTime? MatchedAt { get; set; }
}