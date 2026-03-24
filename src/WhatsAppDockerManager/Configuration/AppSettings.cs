namespace WhatsAppDockerManager.Configuration;

public class AppSettings
{
    public SupabaseSettings Supabase { get; set; } = new();
    public DockerSettings Docker { get; set; } = new();
    public HostSettings Host { get; set; } = new();
    public ProxySettings Proxy { get; set; } = new();
}

public class SupabaseSettings
{
    public string Url { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
}

public class DockerSettings
{
    public string ImageName { get; set; } = "liorgr/whatsapp-single:latest";
    public string DataBasePath { get; set; } = "/opt/whatsapp-data";
    public string Timezone { get; set; } = "Asia/Jerusalem";
}

public class HostSettings
{
    public string HostName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string? ExternalIp { get; set; }
    public int PortRangeStart { get; set; } = 8001;
    public int PortRangeEnd { get; set; } = 8100;
    public int MaxContainers { get; set; } = 50;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int HealthCheckIntervalSeconds { get; set; } = 60;
}

public class ProxySettings
{
    public int HttpPort { get; set; } = 5000;
    public int TcpPortStart { get; set; } = 9001;
    public int TcpPortEnd { get; set; } = 9100;
}
