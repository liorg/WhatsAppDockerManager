using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("messages")]
public class Message : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("call_id")]
    public Guid? CallId { get; set; }


    // ← חסר בקוד הנוכחי!
    [Column("phone_id")]
    public Guid? PhoneId { get; set; }  

    [Column("contact_id")]
    public Guid? ContactId { get; set; }
   
    [Column("sender")]
    public string Sender { get; set; } = string.Empty;

    [Column("content")]
    public string Content { get; set; } = "{}";

    [Column("status")]
    public string? Status { get; set; }

    [Column("sent_at")]
    public DateTime? SentAt { get; set; }

    [Column("leaf_id")]
    public string? LeafId { get; set; }

    [Column("direction")]
    public bool Direction { get; set; }

    [Column("retry_counter")]
    public int RetryCounter { get; set; }

    [Column("tool_tip")]
    public string? ToolTip { get; set; }

    [Column("whatsapp_message_id")]
    public string? WhatsappMessageId { get; set; }

    [Column("media_url")]
    public string? MediaUrl { get; set; }

}
