namespace WhatsAppDockerManager.Services;

/// <summary>
/// לולאת heartbeat — מעדכנת last_heartbeat + משאבי מערכת כל N שניות.
/// </summary>
public class HeartbeatService : BackgroundService
{
    private readonly IContainerManager _containerManager;
    private readonly ISupabaseService  _supabaseService;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly int _intervalSeconds;

    public HeartbeatService(
        IContainerManager containerManager,
        ISupabaseService supabaseService,
        IConfiguration configuration,
        ILogger<HeartbeatService> logger)
    {
        _containerManager = containerManager;
        _supabaseService  = supabaseService;
        _logger           = logger;

        _intervalSeconds = Math.Max(
            configuration.GetValue("AppSettings:Host:HeartbeatSeconds", 20), 5);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("[HEARTBEAT] Starting — every {Seconds}s", _intervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var hostId = _containerManager.CurrentHostId;

                if (hostId.HasValue)
                {
                    var metrics = await SystemMetrics.CollectAsync(ct);
                    await _supabaseService.UpdateHostHeartbeatAsync(hostId.Value, metrics);

                    _logger.LogDebug(
                        "[HEARTBEAT] cpu={Cpu}% ram={RamUsed}/{RamTotal}MB disk={DiskUsed}/{DiskTotal}GB containers={Containers}",
                        metrics.CpuPercent, metrics.RamUsedMb, metrics.RamTotalMb,
                        metrics.DiskUsedGb, metrics.DiskTotalGb, metrics.ContainerCount);
                }
                else
                {
                    // InitializeAsync עוד לא סיים — ננסה שוב בפעימה הבאה
                    _logger.LogDebug("[HEARTBEAT] Host not initialized yet — skipping");
                }
            }
            catch (Exception ex)
            {
                // אף פעם לא זורקים — נפילה כאן תהרוג את הלולאה לתמיד
                _logger.LogError(ex, "[HEARTBEAT] Beat failed");
            }

            try   { await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[HEARTBEAT] Stopped");
    }
}
