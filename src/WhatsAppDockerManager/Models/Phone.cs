using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("phones")]
public class Phone : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    public Guid? UserId { get; set; }

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

    

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
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