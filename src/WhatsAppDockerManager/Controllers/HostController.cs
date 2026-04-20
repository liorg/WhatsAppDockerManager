using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;

namespace WhatsAppDockerManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HostController : ControllerBase
{
    private readonly IContainerManager _containerManager;
    private readonly ISupabaseService _supabaseService;
    private readonly ILogger<HostController> _logger;

    public HostController(
        IContainerManager containerManager,
        ISupabaseService supabaseService,
        ILogger<HostController> logger)
    {
        _containerManager = containerManager;
        _supabaseService = supabaseService;
        _logger = logger;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        if (!_containerManager.CurrentHostId.HasValue)
            return StatusCode(503, new { error = "Host not initialized" });

        var host   = await _supabaseService.GetHostByIdAsync(_containerManager.CurrentHostId.Value);
        var phones = await _supabaseService.GetPhonesForHostAsync(_containerManager.CurrentHostId.Value);

        return Ok(new
        {
            hostId           = host?.Id,
            hostName         = host?.HostName,
            ipAddress        = host?.IpAddress,
            externalIp       = host?.ExternalIp,
            status           = host?.Status,
            lastHeartbeat    = host?.LastHeartbeat,
            activeContainers = phones.Count(p => p.DockerStatus == PhoneDockerStatus.Running),
            totalPhones      = phones.Count,
            maxContainers    = host?.MaxContainers
        });
    }

    [HttpGet("all")]
    public async Task<IActionResult> GetAllHosts()
    {
        var hosts = await _supabaseService.GetActiveHostsAsync();
        return Ok(hosts);
    }

    [HttpPost("sync")]
    public async Task<IActionResult> TriggerSync()
    {
        await _containerManager.SyncContainersAsync();
        return Ok(new { message = "Sync triggered" });
    }

    /// <summary>
    /// קח שליטה על כל הטלפונים של שרת מת לפי כתובת IP חיצונית.
    /// השרת הנוכחי יקים containers, ישחזר creds ויתחבר אוטומטית בלי QR.
    /// </summary>
    [HttpPost("takeover")]
    public async Task<IActionResult> TakeOver([FromQuery] string externalIp)
    {
        if (!_containerManager.CurrentHostId.HasValue)
            return StatusCode(503, new { error = "Host not initialized" });

        if (string.IsNullOrWhiteSpace(externalIp))
            return BadRequest(new { error = "externalIp is required" });

        // ── מצא את ה-host לפי ExternalIp ─────────────────────
        var allHosts = await _supabaseService.GetActiveHostsAsync();
        var deadHost = allHosts.FirstOrDefault(h => h.ExternalIp == externalIp);

        if (deadHost == null)
            return NotFound(new { error = $"No host found with external IP {externalIp}" });

        if (deadHost.Id == _containerManager.CurrentHostId.Value)
            return BadRequest(new { error = "Cannot take over from yourself" });

        var phones = await _supabaseService.GetPhonesForHostAsync(deadHost.Id);

        _logger.LogWarning(
            "Manual takeover: host {CurrentHost} taking over {DeadHost} ({Ip}) — {PhoneCount} phones",
            _containerManager.CurrentHostId.Value, deadHost.HostName, externalIp, phones.Count);

        // ── הפעל takeover ברקע ────────────────────────────────
        _ = Task.Run(() => _containerManager.TakeOverFromDeadHostAsync(deadHost.Id));

        return Ok(new
        {
            message      = $"Takeover started for host {externalIp}",
            deadHostId   = deadHost.Id,
            deadHostName = deadHost.HostName,
            deadHostIp   = externalIp,
            phoneCount   = phones.Count,
            withCreds    = phones.Count(p => !string.IsNullOrEmpty(p.CredsBase64)),
            withoutCreds = phones.Count(p => string.IsNullOrEmpty(p.CredsBase64)),
            takenByHost  = _containerManager.CurrentHostId.Value
        });
    }

    /// <summary>
    /// רשימת hosts עם מצב heartbeat — לזיהוי hosts מתים
    /// </summary>
    [HttpGet("dead")]
    public async Task<IActionResult> GetDeadHosts([FromQuery] int timeoutMinutes = 5)
    {
        var deadHosts = await _supabaseService.GetDeadHostsAsync(timeoutMinutes);
        return Ok(new
        {
            count     = deadHosts.Count,
            timeout   = $"{timeoutMinutes} minutes",
            deadHosts = deadHosts.Select(h => new
            {
                id            = h.Id,
                hostName      = h.HostName,
                externalIp    = h.ExternalIp,
                lastHeartbeat = h.LastHeartbeat,
                minutesSinceHeartbeat = (DateTime.UtcNow - h.LastHeartbeat).TotalMinutes
            })
        });
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }
}