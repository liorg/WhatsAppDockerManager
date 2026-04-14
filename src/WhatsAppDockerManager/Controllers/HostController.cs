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

        var host = await _supabaseService.GetHostByIdAsync(_containerManager.CurrentHostId.Value);
        var phones = await _supabaseService.GetPhonesForHostAsync(_containerManager.CurrentHostId.Value);

        return Ok(new
        {
            hostId = host?.Id,
            hostName = host?.HostName,
            status = host?.Status,
            lastHeartbeat = host?.LastHeartbeat,
            activeContainers = phones.Count(p => p.DockerStatus == PhoneDockerStatus.Running),
            totalPhones = phones.Count,
            maxContainers = host?.MaxContainers
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

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }
}
