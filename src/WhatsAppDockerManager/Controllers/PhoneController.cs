using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;

namespace WhatsAppDockerManager.Controllers;

[ApiController]
[Route("api/phones")]
public class PhoneController : ControllerBase
{
    private readonly IContainerManager _containerManager;
    private readonly ISupabaseService _supabaseService;
    private readonly IDockerService _dockerService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PhoneController> _logger;

    public PhoneController(
        IContainerManager containerManager,
        ISupabaseService supabaseService,
        IDockerService dockerService,
        IConfiguration configuration,
        ILogger<PhoneController> logger)
    {
        _containerManager = containerManager;
        _supabaseService  = supabaseService;
        _dockerService    = dockerService;
        _configuration    = configuration;
        _logger           = logger;
    }

    // ── POST /api/phones/provision ────────────────────────────────────────────
    // יוצר טלפון + מקים קונטיינר אם לא קיים + מחזיר QR או סטטוס connected
    [HttpPost("provision")]
    public async Task<IActionResult> Provision([FromBody] ProvisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneNumber))
            return BadRequest(new { error = "phoneNumber is required" });

        var normalizedPhone = NormalizePhone(request.PhoneNumber);

        // ── חשב ports לפי hash ──────────────────────────────────────────────
        var (fastApiPort, baileysPort) = PortHashCalculator.GetBothPorts(
            normalizedPhone, _configuration);

        _logger.LogInformation(
            "Provision request: {Phone} → FastAPI:{FastApi} Baileys:{Baileys}",
            normalizedPhone, fastApiPort, baileysPort);

        // ── בדוק אם הטלפון כבר קיים ב-DB ───────────────────────────────────
        var existingPhone = await _supabaseService.GetPhoneByNumberAsync(normalizedPhone);

        Phone phone;
        if (existingPhone != null)
        {
            phone = existingPhone;
            _logger.LogInformation("Phone {Phone} already exists in DB", normalizedPhone);
        }
        else
        {
            // צור רשומה חדשה בDB
            phone = await _supabaseService.CreatePhoneAsync(new Phone
            {
                Id           = Guid.NewGuid(),
                Number       = normalizedPhone,
                Label        = request.Nickname,
                Color        = request.Tag,
                Status       = "active",
                DockerStatus = PhoneDockerStatus.Pending,
                ApiPort      = fastApiPort,
                WsPort       = baileysPort,
            });

            _logger.LogInformation("Created new phone record for {Phone}", normalizedPhone);
        }

        // ── בדוק אם הקונטיינר רץ ───────────────────────────────────────────
        var containerRunning = !string.IsNullOrEmpty(phone.ContainerId)
            && await _dockerService.IsContainerRunningAsync(phone.ContainerId);

        if (!containerRunning)
        {
            _logger.LogInformation("Container not running for {Phone}, starting...", normalizedPhone);
            var started = await _containerManager.StartPhoneContainerAsync(phone);

            if (!started)
                return StatusCode(500, new { error = "Failed to start container" });

            // המתן קצת שה-FastAPI יעלה
            await Task.Delay(3000);
        }

        // ── בדוק סטטוס חיבור מהקונטיינר ────────────────────────────────────
        var waStatus = await GetContainerStatus(fastApiPort);

        if (waStatus == "connected")
        {
            return Ok(new ProvisionResponse
            {
                PhoneId      = phone.Id,
                PhoneNumber  = normalizedPhone,
                Label        = phone.Label,
                Color        = phone.Color,
                FastApiPort  = fastApiPort,
                BaileysPort  = baileysPort,
                Status       = "connected",
                QrCode       = null,
                QrImageBase64 = null,
                Message      = "Phone is already connected"
            });
        }

        // ── שלוף QR ─────────────────────────────────────────────────────────
        var qrData = await GetContainerQr(fastApiPort);

        return Ok(new ProvisionResponse
        {
            PhoneId       = phone.Id,
            PhoneNumber   = normalizedPhone,
            Label         = phone.Label,
            Color         = phone.Color,
            FastApiPort   = fastApiPort,
            BaileysPort   = baileysPort,
            Status        = "qr_ready",
            QrCode        = qrData?.Qr,
            QrImageBase64 = qrData?.QrImageBase64,
            QrRefreshUrl  = $"/api/phones/{phone.Id}/qrcode",
            Message       = "Scan the QR code to connect"
        });
    }

    // ── GET /api/phones/{id}/qrcode ───────────────────────────────────────────
    // refresh endpoint — מחזיר QR עדכני או connected
    [HttpGet("{id:guid}/qrcode")]
    public async Task<IActionResult> GetQrCode(Guid id)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(id);
        if (phone == null)
            return NotFound(new { error = "Phone not found" });

        var (fastApiPort, _) = PortHashCalculator.GetBothPorts(
            phone.Number, _configuration);

        var waStatus = await GetContainerStatus(fastApiPort);

        if (waStatus == "connected")
        {
            return Ok(new { status = "connected", message = "Phone is connected" });
        }

        var qrData = await GetContainerQr(fastApiPort);
        if (qrData == null)
            return StatusCode(503, new { error = "Container not ready yet", status = waStatus });

        return Ok(new
        {
            status        = "qr_ready",
            qr            = qrData.Qr,
            qrImageBase64 = qrData.QrImageBase64,
        });
    }

    // ── GET /api/phones/{id}/qrcode/image ────────────────────────────────────
    // מחזיר PNG ישירות — אפשר לפתוח בדפדפן
    [HttpGet("{id:guid}/qrcode/image")]
    public async Task<IActionResult> GetQrCodeImage(Guid id)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(id);
        if (phone == null) return NotFound();

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

    // ── GET /api/phones ───────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> ListPhones()
    {
        var phones = await _supabaseService.GetAllPhonesAsync();
        return Ok(phones.Select(p => new
        {
            p.Id, p.Number, p.Label, p.Color,
            p.DockerStatus, p.ContainerId,
            FastApiPort = PortHashCalculator.GetFastApiPort(p.Number, _configuration),
            BaileysPort = PortHashCalculator.GetBaileysPort(p.Number, _configuration),
        }));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> GetContainerStatus(int fastApiPort)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var res  = await http.GetFromJsonAsync<ContainerStatusResponse>(
                $"http://localhost:{fastApiPort}/status");
            return res?.Status ?? "unknown";
        }
        catch { return "unavailable"; }
    }

    private async Task<ContainerQrResponse?> GetContainerQr(int fastApiPort)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return await http.GetFromJsonAsync<ContainerQrResponse>(
                $"http://localhost:{fastApiPort}/qrcode");
        }
        catch { return null; }
    }

    private static string NormalizePhone(string phone)
        => "+" + new string(phone.Where(char.IsDigit).ToArray());
}

// ── Request / Response models ─────────────────────────────────────────────────

public record ProvisionRequest
{
    public string PhoneNumber { get; init; } = "";
    public string? Nickname   { get; init; }  // נשמר ב-Label
    public string? Tag        { get; init; }  // נשמר ב-Color
}

public record ProvisionResponse
{
    public Guid    PhoneId       { get; init; }
    public string  PhoneNumber   { get; init; } = "";
    public string? Label         { get; init; }
    public string? Color         { get; init; }
    public int     FastApiPort   { get; init; }
    public int     BaileysPort   { get; init; }
    public string  Status        { get; init; } = "";
    public string? QrCode        { get; init; }
    public string? QrImageBase64 { get; init; }
    public string? QrRefreshUrl  { get; init; }
    public string  Message       { get; init; } = "";
}

// DTOs מה-FastAPI container
record ContainerStatusResponse(string Status);
record ContainerQrResponse(string? Qr, string? QrImageBase64, string? Status);