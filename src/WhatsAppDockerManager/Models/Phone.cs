using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("user_emails")]
public class UserEmail : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("email")]
    public string? Email { get; set; }
}

[Table("phones")]
public class Phone : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    public Guid? UserId { get; set; }

[Column("auth_revision")]
public int AuthRevision { get; set; } = 0;

    [Column("number")]
    public string Number { get; set; } = string.Empty;

    [Column("label")]
    public string? Label { get; set; }

    [Column("color")]
    public string? Color { get; set; }

    [Column("status")]
    public string Status { get; set; } = "active";

    [Column("docker_url")]
    public string? DockerUrl { get; set; }

    [Column("docker_status")]
    public string DockerStatus { get; set; } = "unknown";

    [Column("host_id")]
    public Guid? HostId { get; set; }

    [Column("container_id")]
    public string? ContainerId { get; set; }

    [Column("container_name")]
    public string? ContainerName { get; set; }

    [Column("api_port")]
    public int? ApiPort { get; set; }

    [Column("ws_port")]
    public int? WsPort { get; set; }

    [Column("last_health_check")]
    public DateTime? LastHealthCheck { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("creds_base64")]
    public string? CredsBase64 { get; set; }

    [Column("auth_session_id")]
   public string? AuthSessionId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("pairing_code")]        public string?   PairingCode       { get; set; }
    [Column("pairing_code_expiry")] public DateTime? PairingCodeExpiry { get; set; }
    [Column("use_pairing_code")]    public bool      UsePairingCode    { get; set; }
    [Column("creds_updated_at")]    public DateTime? CredsUpdatedAt    { get; set; }


    // ════════════════════════════════════════════════════════════════════════════
// Models/Phone.cs — שתי עמודות חדשות
// ════════════════════════════════════════════════════════════════════════════
// הוסף לתוך המחלקה Phone, ליד שאר ה-[Column].
// שתיהן קיימות ב-DB אחרי הרצת 01-database/2026-08-29_templates.sql
// עם NOT NULL DEFAULT, לכן אין צורך ב-nullable.

    /// <summary>baileys = WA socket לא רשמי | whatsapp = Cloud API רשמי</summary>
    [Column("provider")]
    public string Provider { get; set; } = "baileys";

    /// <summary>שפת ברירת מחדל לתבניות של הטלפון.</summary>
    [Column("lang")]
    public string Lang { get; set; } = "he";
}

public static class PhoneDockerStatus
{
    public const string Unknown = "unknown";
    public const string Pending = "pending";
    public const string Pulling = "pulling";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Stopped = "stopped";
    public const string Error = "error";
}
