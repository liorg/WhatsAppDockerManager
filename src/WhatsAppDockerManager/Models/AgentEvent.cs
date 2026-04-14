using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("agent_events")]
public class AgentEvent : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("agent_host_id")]
    public Guid? AgentHostId { get; set; }

    [Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    [Column("event_data")]
    public string? EventData { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}

public static class AgentEventType
{
    public const string Created = "created";
    public const string Started = "started";
    public const string Stopped = "stopped";
    public const string Removed = "removed";
    public const string Error = "error";
    public const string HealthCheckPassed = "health_check_passed";
    public const string HealthCheckFailed = "health_check_failed";
    public const string Migrated = "migrated";
}
