// Models/WebhookRegistration.cs
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;
public static class WebhookRegistrationType
{
    public const string Recording = "recording";  // שיחה חיה — React צופה
    public const string Job       = "job";         // תהליך אוטומטי — שולח שיחות
}

[Table("webhook_registrations")]
public class WebhookRegistration : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("phone_id")]
    public Guid PhoneId { get; set; }

    [Column("contact_id")]
    public Guid ContactId { get; set; }

    [Column("callback_url")]
    public string CallbackUrl { get; set; } = "";

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

        [Column("type")]
    public string Type { get; set; } = WebhookRegistrationType.Recording;

}