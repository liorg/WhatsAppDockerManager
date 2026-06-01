// Models/WebhookRegistration.cs
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

public static class WebhookRegistrationType
{  
    public const string Trigger   = "trigger";    // ← עדיפות עליונה
    public const string Job       = "job";        // scheduler
    public const string Recording = "recording";  // הקלטה
}
[Table("webhook_registrations")]
public class WebhookRegistration : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("callback_url")]
    public string CallbackUrl { get; set; } = "";

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("type")]
    public string Type { get; set; } = WebhookRegistrationType.Recording;

    [Column("status")]
    public string Status { get; set; } = "active";   // ← חדש: draft | active | published

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}