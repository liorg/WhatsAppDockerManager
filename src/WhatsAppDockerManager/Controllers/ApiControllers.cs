using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;
using WhatsAppDockerManager.Services.Proxy;

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

    /// <summary>
    /// Get current host status
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        if (!_containerManager.CurrentHostId.HasValue)
        {
            return StatusCode(503, new { error = "Host not initialized" });
        }

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

    /// <summary>
    /// Get all active hosts
    /// </summary>
    [HttpGet("all")]
    public async Task<IActionResult> GetAllHosts()
    {
        var hosts = await _supabaseService.GetActiveHostsAsync();
        return Ok(hosts);
    }

    /// <summary>
    /// Trigger container sync
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> TriggerSync()
    {
        await _containerManager.SyncContainersAsync();
        return Ok(new { message = "Sync triggered" });
    }

    /// <summary>
    /// Health endpoint
    /// </summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }
}

/// <summary>
/// Routes Controller - Returns all available WhatsApp routes for Frontend
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class RoutesController : ControllerBase
{
    private readonly DynamicProxyConfigProvider _proxyConfig;
    private readonly ISupabaseService _supabaseService;
    private readonly IContainerManager _containerManager;

    public RoutesController(
        DynamicProxyConfigProvider proxyConfig,
        ISupabaseService supabaseService,
        IContainerManager containerManager)
    {
        _proxyConfig = proxyConfig;
        _supabaseService = supabaseService;
        _containerManager = containerManager;
    }

    /// <summary>
    /// Get all available WhatsApp routes for Frontend.
    /// Frontend can use these to know which phones are available.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetRoutes()
    {
        if (!_containerManager.CurrentHostId.HasValue)
        {
            return StatusCode(503, new { error = "Host not initialized" });
        }

        var phones = await _supabaseService.GetPhonesForHostAsync(_containerManager.CurrentHostId.Value);
        
        var routes = phones
            .Where(p => p.DockerStatus == PhoneDockerStatus.Running && p.ApiPort.HasValue)
            .Select(p => new
            {
                phoneId = p.Id,
                phoneNumber = p.Number,
                label = p.Label,
                status = p.DockerStatus,
                // Routes available for this phone
                routes = new
                {
                    baseUrl = $"/wa/{p.Number.Replace("+", "")}",
                    byId = $"/wa/id/{p.Id}",
                    // Specific endpoints
                    endpoints = new
                    {
                        status = $"/wa/{p.Number.Replace("+", "")}/status",
                        qrcode = $"/wa/{p.Number.Replace("+", "")}/qrcode",
                        qrcodeImage = $"/wa/{p.Number.Replace("+", "")}/qrcode/image",
                        sendText = $"/wa/{p.Number.Replace("+", "")}/send/text",
                        sendButtons = $"/wa/{p.Number.Replace("+", "")}/send/buttons",
                        sendList = $"/wa/{p.Number.Replace("+", "")}/send/list",
                        contacts = $"/wa/{p.Number.Replace("+", "")}/contacts",
                        authDashboard = $"/wa/{p.Number.Replace("+", "")}/auth/dashboard",
                        health = $"/wa/{p.Number.Replace("+", "")}/health",
                        webhooksRegister = $"/wa/{p.Number.Replace("+", "")}/webhooks/register",
                        messagesStream = $"/wa/{p.Number.Replace("+", "")}/messages/stream/read"
                    }
                },
                lastHealthCheck = p.LastHealthCheck,
                errorMessage = p.ErrorMessage
            })
            .ToList();

        return Ok(new
        {
            count = routes.Count,
            phones = routes
        });
    }

    /// <summary>
    /// Get route info for a specific phone by number
    /// </summary>
    [HttpGet("phone/{phoneNumber}")]
    public IActionResult GetRouteByPhone(string phoneNumber)
    {
        var phoneKey = phoneNumber.Replace("+", "");
        var route = _proxyConfig.GetRouteInfo(phoneKey);
        
        if (route == null)
        {
            return NotFound(new { error = $"No route found for phone {phoneNumber}" });
        }

        return Ok(new
        {
            phoneId = route.PhoneId,
            phoneNumber = route.PhoneNumber,
            baseUrl = $"/wa/{phoneKey}",
            apiPort = route.ApiPort,
            wsPort = route.WsPort
        });
    }

    /// <summary>
    /// Get route info for a specific phone by ID
    /// </summary>
    [HttpGet("id/{phoneId}")]
    public IActionResult GetRouteById(Guid phoneId)
    {
        var route = _proxyConfig.GetRouteInfoById(phoneId);
        
        if (route == null)
        {
            return NotFound(new { error = $"No route found for phone ID {phoneId}" });
        }

        var phoneKey = route.PhoneNumber.Replace("+", "");
        return Ok(new
        {
            phoneId = route.PhoneId,
            phoneNumber = route.PhoneNumber,
            baseUrl = $"/wa/{phoneKey}",
            apiPort = route.ApiPort,
            wsPort = route.WsPort
        });
    }
}

[ApiController]
[Route("api/[controller]")]
public class PhonesController : ControllerBase
{
    private readonly IContainerManager _containerManager;
    private readonly ISupabaseService _supabaseService;
    private readonly ILogger<PhonesController> _logger;

    public PhonesController(
        IContainerManager containerManager,
        ISupabaseService supabaseService,
        ILogger<PhonesController> logger)
    {
        _containerManager = containerManager;
        _supabaseService = supabaseService;
        _logger = logger;
    }

    /// <summary>
    /// Get all phones on this host
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPhones()
    {
        if (!_containerManager.CurrentHostId.HasValue)
        {
            return StatusCode(503, new { error = "Host not initialized" });
        }

        var phones = await _supabaseService.GetPhonesForHostAsync(_containerManager.CurrentHostId.Value);
        return Ok(phones.Select(p => new
        {
            p.Id,
            p.Number,
            p.Label,
            p.DockerStatus,
            p.DockerUrl,
            p.ApiPort,
            p.WsPort,
            p.ContainerId,
            p.LastHealthCheck,
            p.ErrorMessage
        }));
    }

    /// <summary>
    /// Get specific phone
    /// </summary>
    [HttpGet("{phoneId}")]
    public async Task<IActionResult> GetPhone(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
        {
            return NotFound();
        }
        return Ok(phone);
    }

    /// <summary>
    /// Start a phone container
    /// </summary>
    [HttpPost("{phoneId}/start")]
    public async Task<IActionResult> StartPhone(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
        {
            return NotFound();
        }

        // Assign to this host if not assigned
        if (phone.HostId == null && _containerManager.CurrentHostId.HasValue)
        {
            await _supabaseService.AssignPhoneToHostAsync(phoneId, _containerManager.CurrentHostId.Value);
            phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        }

        var success = await _containerManager.StartPhoneContainerAsync(phone!);
        
        if (success)
        {
            return Ok(new { message = "Container started" });
        }
        return StatusCode(500, new { error = "Failed to start container" });
    }

    /// <summary>
    /// Stop a phone container
    /// </summary>
    [HttpPost("{phoneId}/stop")]
    public async Task<IActionResult> StopPhone(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
        {
            return NotFound();
        }

        var success = await _containerManager.StopPhoneContainerAsync(phone);
        
        if (success)
        {
            return Ok(new { message = "Container stopped" });
        }
        return StatusCode(500, new { error = "Failed to stop container" });
    }

    /// <summary>
    /// Restart a phone container
    /// </summary>
    [HttpPost("{phoneId}/restart")]
    public async Task<IActionResult> RestartPhone(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
        {
            return NotFound();
        }

        var success = await _containerManager.RestartPhoneContainerAsync(phone);
        
        if (success)
        {
            return Ok(new { message = "Container restarted" });
        }
        return StatusCode(500, new { error = "Failed to restart container" });
    }
}

[ApiController]
[Route("api/[controller]")]
public class WebhookController : ControllerBase
{
    private readonly IContainerManager _containerManager;
    private readonly ISupabaseService _supabaseService;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(
        IContainerManager containerManager,
        ISupabaseService supabaseService,
        ILogger<WebhookController> logger)
    {
        _containerManager = containerManager;
        _supabaseService = supabaseService;
        _logger = logger;
    }

    /// <summary>
    /// Webhook endpoint for Docker containers to report status
    /// </summary>
    [HttpPost("container-status")]
    public async Task<IActionResult> ContainerStatus([FromBody] ContainerStatusWebhook payload)
    {
        _logger.LogInformation("Received container status webhook: {PhoneId} - {Status}", 
            payload.PhoneId, payload.Status);

        var phone = await _supabaseService.GetPhoneByIdAsync(payload.PhoneId);
        if (phone == null)
        {
            return NotFound(new { error = "Phone not found" });
        }

        await _supabaseService.UpdatePhoneDockerStatusAsync(
            payload.PhoneId,
            payload.Status,
            errorMessage: payload.Error
        );

        await _supabaseService.LogContainerEventAsync(
            payload.PhoneId,
            _containerManager.CurrentHostId,
            payload.EventType ?? "status_update",
            payload
        );

        return Ok(new { received = true });
    }

    /// <summary>
    /// Webhook endpoint for receiving events from WhatsApp containers.
    /// This is where containers send their events (messages, auth status, etc.)
    /// </summary>
    [HttpPost("container-event/{phoneId}")]
    public async Task<IActionResult> ContainerEvent(Guid phoneId, [FromBody] ContainerEventPayload payload)
    {
        _logger.LogDebug("Received container event for phone {PhoneId}: {Event}", 
            phoneId, payload.Event);

        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
        {
            return NotFound(new { error = "Phone not found" });
        }

        // Handle specific events
        switch (payload.Event)
        {
            case "authenticated":
                _logger.LogInformation("Phone {PhoneId} authenticated as {Phone}", 
                    phoneId, payload.Phone);
                await _supabaseService.UpdatePhoneDockerStatusAsync(
                    phoneId, 
                    PhoneDockerStatus.Running
                );
                break;

            case "disconnected":
                _logger.LogWarning("Phone {PhoneId} disconnected", phoneId);
                await _supabaseService.UpdatePhoneDockerStatusAsync(
                    phoneId,
                    PhoneDockerStatus.Error,
                    errorMessage: "WhatsApp disconnected"
                );
                break;

            case "qr":
                _logger.LogInformation("Phone {PhoneId} waiting for QR scan", phoneId);
                break;

            case "message":
                // Log incoming message event
                _logger.LogDebug("Phone {PhoneId} received message from {Jid}", 
                    phoneId, payload.Jid);
                break;
        }

        // Log the event
        await _supabaseService.LogContainerEventAsync(
            phoneId,
            _containerManager.CurrentHostId,
            payload.Event ?? "unknown",
            payload
        );

        return Ok(new { received = true });
    }

    /// <summary>
    /// Webhook for host registration (from other hosts)
    /// </summary>
    [HttpPost("host-register")]
    public async Task<IActionResult> HostRegister([FromBody] HostRegisterWebhook payload)
    {
        _logger.LogInformation("Received host registration: {HostName}", payload.HostName);

        // This is informational - actual registration happens in the host itself
        return Ok(new { received = true, currentHost = _containerManager.CurrentHostId });
    }
}

// Webhook DTOs
public class ContainerStatusWebhook
{
    public Guid PhoneId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? EventType { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Payload sent by WhatsApp containers via webhook
/// Matches the format from main.py's /internal/baileys-event
/// </summary>
public class ContainerEventPayload
{
    public string? Event { get; set; }
    public string? MessageId { get; set; }
    public string? Jid { get; set; }
    public string? Type { get; set; }
    public Dictionary<string, object>? Data { get; set; }
    public long? Timestamp { get; set; }
    
    // For authenticated event
    public string? Phone { get; set; }
    public string? Name { get; set; }
    public string? CredsB64 { get; set; }
}

public class HostRegisterWebhook
{
    public string HostName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string? ExternalIp { get; set; }
}
