using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("agent_hosts")]
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

public static class HostStatus
{
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string Maintenance = "maintenance";
}
