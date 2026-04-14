using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;

namespace WhatsAppDockerManager.Controllers;

/// <summary>
/// Phones API - Get phone info and provision new phones
/// </summary>
[ApiController]
[Route("api/phones")]
public class PhonesController : ControllerBase
{
    private readonly ISupabaseService _supabaseService;
    private readonly IContainerManager _containerManager;
    private readonly IDockerService _dockerService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PhonesController> _logger;

    public PhonesController(
        ISupabaseService supabaseService,
        IContainerManager containerManager,
        IDockerService dockerService,
        IConfiguration configuration,
        ILogger<PhonesController> logger)
    {
        _supabaseService = supabaseService;
        _containerManager = containerManager;
        _dockerService = dockerService;
        _configuration = configuration;
        _logger = logger;
    }
/// <summary>
/// Logout and delete auth files - for fresh QR scan
/// </summary>
[HttpPost("{phoneId}/logout")]
public async Task<IActionResult> Logout(Guid phoneId)
{
    var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
    if (phone == null)
        return NotFound(new { error = "Phone not found" });

    try
    {
        // 1. Stop container
        if (!string.IsNullOrEmpty(phone.ContainerId))
        {
            await _dockerService.StopContainerAsync(phone.ContainerId);
        }

        // 2. Delete auth files
        var phoneIndex = phone.Number.Replace("+", "")
            .Substring(Math.Max(0, phone.Number.Replace("+", "").Length - 3));
        var authPath = Path.Combine(_configuration["AppSettings:Docker:DataBasePath"] ?? "/opt/whatsapp-data", $"auth_{phoneIndex}");
        
        if (Directory.Exists(authPath))
        {
            Directory.Delete(authPath, recursive: true);
            Directory.CreateDirectory(authPath);
            _logger.LogInformation("Deleted auth files at {Path}", authPath);
        }

        // 3. Update status
        await _supabaseService.UpdatePhoneDockerStatusAsync(phoneId, PhoneDockerStatus.Pending);

        // 4. Restart container
        await _containerManager.StartPhoneContainerAsync(phone);

        return Ok(new { 
            success = true, 
            message = "Logged out. Wait 10 seconds then get new QR.",
            qrUrl = $"/api/phones/{phoneId}/qrcode"
        });
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error during logout for phone {PhoneId}", phoneId);
        return StatusCode(500, new { error = ex.Message });
    }
}
    [HttpGet]
    public async Task<IActionResult> GetAllPhones()
    {
        var phones = await _supabaseService.GetAllPhonesAsync();
        return Ok(new
        {
            count = phones.Count,
            phones = phones.Select(p => new
            {
                id = p.Id,
                number = p.Number,
                label = p.Label,
                dockerStatus = p.DockerStatus,
                apiPort = p.ApiPort
            })
        });
    }

    [HttpGet("{phoneId}")]
    public async Task<IActionResult> GetPhone(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });

        return Ok(new
        {
            id = phone.Id,
            number = phone.Number,
            label = phone.Label,
            dockerStatus = phone.DockerStatus,
            apiPort = phone.ApiPort,
            lastHealthCheck = phone.LastHealthCheck
        });
    }

    /// <summary>
    /// Provision a phone - create record + start container + return QR or connected status
    /// </summary>
    [HttpPost("provision")]
    public async Task<IActionResult> Provision([FromBody] ProvisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneNumber))
            return BadRequest(new { error = "phoneNumber is required" });

        var normalizedPhone = NormalizePhone(request.PhoneNumber);
        var fastApiPort = PortHashCalculator.GetFastApiPort(normalizedPhone, _configuration);

        _logger.LogInformation("Provision request: {Phone} → Port:{Port}", normalizedPhone, fastApiPort);

        // Check if phone exists in DB
        var existingPhone = await _supabaseService.GetPhoneByNumberAsync(normalizedPhone);

        Phone phone;
        if (existingPhone != null)
        {
            phone = existingPhone;
            _logger.LogInformation("Phone {Phone} already exists in DB", normalizedPhone);
        }
        else
        {
            phone = await _supabaseService.CreatePhoneAsync(new Phone
            {
                Id = Guid.NewGuid(),
                Number = normalizedPhone,
                Label = request.Nickname,
                Color = request.Tag,
                Status = "active",
                DockerStatus = PhoneDockerStatus.Pending,
                ApiPort = fastApiPort,
            });
            _logger.LogInformation("Created new phone record for {Phone}", normalizedPhone);
        }

        // Check if container is running
        var containerRunning = !string.IsNullOrEmpty(phone.ContainerId)
            && await _dockerService.IsContainerRunningAsync(phone.ContainerId);

        if (!containerRunning)
        {
            _logger.LogInformation("Container not running for {Phone}, starting...", normalizedPhone);
            var started = await _containerManager.StartPhoneContainerAsync(phone);

            if (!started)
                return StatusCode(500, new { error = "Failed to start container" });

            await Task.Delay(3000);
        }

        // Check connection status
        var waStatus = await GetContainerStatus(fastApiPort);

        if (waStatus == "connected")
        {
            return Ok(new ProvisionResponse
            {
                PhoneId = phone.Id,
                PhoneNumber = normalizedPhone,
                Label = phone.Label,
                Color = phone.Color,
                Port = fastApiPort,
                Status = "connected",
                QrCode = null,
                QrImageBase64 = null,
                Message = "Phone is already connected"
            });
        }

        // Get QR
        var qrData = await GetContainerQr(fastApiPort);

        return Ok(new ProvisionResponse
        {
            PhoneId = phone.Id,
            PhoneNumber = normalizedPhone,
            Label = phone.Label,
            Color = phone.Color,
            Port = fastApiPort,
            Status = "qr_ready",
            QrCode = qrData?.Qr,
            QrImageBase64 = qrData?.QrImageBase64,
            QrRefreshUrl = $"/api/phones/{phone.Id}/qrcode",
            Message = "Scan the QR code to connect"
        });
    }

    /// <summary>
    /// Get QR code for a phone (refresh endpoint)
    /// </summary>
    [HttpGet("{id:guid}/qrcode")]
    public async Task<IActionResult> GetQrCode(Guid id)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(id);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });

        var (fastApiPort, _) = PortHashCalculator.GetBothPorts(phone.Number, _configuration);
        var waStatus = await GetContainerStatus(fastApiPort);

        if (waStatus == "connected")
            return Ok(new { status = "connected", message = "Phone is connected" });

        var qrData = await GetContainerQr(fastApiPort);
        if (qrData == null)
            return StatusCode(503, new { error = "Container not ready yet", status = waStatus });

        return Ok(new
        {
            status = "qr_ready",
            qr = qrData.Qr,
            qrImageBase64 = qrData.QrImageBase64,
        });
    }

    /// <summary>
    /// Get QR code as PNG image
    /// </summary>
    [HttpGet("{id:guid}/qrcode/image")]
    public async Task<IActionResult> GetQrCodeImage(Guid id)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(id);
        if (phone == null)
            return NotFound();

        var (fastApiPort, _) = PortHashCalculator.GetBothPorts(phone.Number, _configuration);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var bytes = await http.GetByteArrayAsync($"http://localhost:{fastApiPort}/qrcode/image");
            return File(bytes, "image/png");
        }
        catch
        {
            return StatusCode(503, new { error = "QR not available yet" });
        }
    }

    private async Task<string> GetContainerStatus(int fastApiPort)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var res = await http.GetFromJsonAsync<ContainerStatusResponse>($"http://localhost:{fastApiPort}/status");
            return res?.Status ?? "unknown";
        }
        catch { return "unavailable"; }
    }

    private async Task<ContainerQrResponse?> GetContainerQr(int fastApiPort)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return await http.GetFromJsonAsync<ContainerQrResponse>($"http://localhost:{fastApiPort}/qrcode");
        }
        catch { return null; }
    }

    private static string NormalizePhone(string phone)
        => "+" + new string(phone.Where(char.IsDigit).ToArray());
}

// DTOs
public record ProvisionRequest
{
    public string PhoneNumber { get; init; } = "";
    public string? Nickname { get; init; }
    public string? Tag { get; init; }
}

public record ProvisionResponse
{
    public Guid PhoneId { get; init; }
    public string PhoneNumber { get; init; } = "";
    public string? Label { get; init; }
    public string? Color { get; init; }
    public int Port { get; init; }
    public string Status { get; init; } = "";
    public string? QrCode { get; init; }
    public string? QrImageBase64 { get; init; }
    public string? QrRefreshUrl { get; init; }
    public string Message { get; init; } = "";
}

record ContainerStatusResponse(string Status);
record ContainerQrResponse(string? Qr, string? QrImageBase64, string? Status);
