using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("heartbeatphones")]
public class HeartbeatPhone : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("status")]
    public string? Status { get; set; }

    [Column("phone_number")]
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>WhatsApp LID of the prober. Identity is by uid, never by number.</summary>
    [Column("uid")]
    public string? Uid { get; set; }

    /// <summary>Emoji the prober sends to announce itself.</summary>
    [Column("identification_type")]
    public string? IdentificationType { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }
}
