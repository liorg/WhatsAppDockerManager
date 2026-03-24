using Postgrest.Attributes;
using Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("hosts")]
public class Host : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("host_name")]
    public string HostName { get; set; } = string.Empty;

    [Column("ip_address")]
    public string IpAddress { get; set; } = string.Empty;

    [Column("external_ip")]
    public string? ExternalIp { get; set; }

    [Column("status")]
    public string Status { get; set; } = "active";

    [Column("last_heartbeat")]
    public DateTime LastHeartbeat { get; set; }

    [Column("max_containers")]
    public int MaxContainers { get; set; } = 50;

    [Column("port_range_start")]
    public int PortRangeStart { get; set; } = 8001;

    [Column("port_range_end")]
    public int PortRangeEnd { get; set; } = 8100;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

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

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}

[Table("container_events")]
public class ContainerEvent : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("phone_id")]
    public Guid? PhoneId { get; set; }

    [Column("host_id")]
    public Guid? HostId { get; set; }

    [Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    [Column("event_data")]
    public string? EventData { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}

// Enum helpers
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

public static class HostStatus
{
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string Maintenance = "maintenance";
}

public static class ContainerEventType
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
