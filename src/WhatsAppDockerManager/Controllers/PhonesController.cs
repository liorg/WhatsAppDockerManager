using Microsoft.AspNetCore.Mvc;
using PhoneNumbers;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;

namespace WhatsAppDockerManager.Controllers;

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
        _supabaseService  = supabaseService;
        _containerManager = containerManager;
        _dockerService    = dockerService;
        _configuration    = configuration;
        _logger           = logger;
    }



    [HttpPost("{phoneId}/logout")]
    public async Task<IActionResult> Logout(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null) return NotFound(new { error = "Phone not found" });

        try
        {
            if (!string.IsNullOrEmpty(phone.ContainerId))
                await _dockerService.RemoveContainerAsync(phone.ContainerId);

            var phoneIndex = phone.Number.Replace("+", "");
            var basePath   = _configuration["AppSettings:Docker:DataBasePath"] ?? "/opt/whatsapp-data";
            var authPath = Path.Combine(basePath, $"auth_{phone.Id}");
            if (Directory.Exists(authPath)) Directory.Delete(authPath, recursive: true);
            Directory.CreateDirectory(authPath);

            await _supabaseService.ClearPhoneForLogoutAsync(phoneId);

            var freshPhone = await _supabaseService.GetPhoneByIdAsync(phoneId);
            if (freshPhone == null) return NotFound(new { error = "Phone not found after update" });

            await _containerManager.StartPhoneContainerAsync(freshPhone);
            return Ok(new { success = true, message = "Logged out. Get new QR.", qrUrl = $"/api/phones/{phoneId}/qrcode" });
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
            count  = phones.Count,
            phones = phones.Select(p => new { id = p.Id, number = p.Number, label = p.Label, dockerStatus = p.DockerStatus, apiPort = p.ApiPort })
        });
    }

    [HttpGet("{phoneId}")]
    public async Task<IActionResult> GetPhone(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null) return NotFound(new { error = "Phone not found" });
        return Ok(new { id = phone.Id, number = phone.Number, label = phone.Label, dockerStatus = phone.DockerStatus, apiPort = phone.ApiPort, lastHealthCheck = phone.LastHealthCheck });
    }
    // ══════════════════════════════════════════════════════════════════
    //journalctl -u whatsapp-manager.service -f --no-pager | grep "PROVISION"

    // ══════════════════════════════════════════════════════════════════
    // Provision — upsert לפי number + user_id
    // ══════════════════════════════════════════════════════════════════
  [HttpPost("provision")]
    public async Task<IActionResult> Provision([FromBody] ProvisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneNumber))
            return BadRequest(new { error = "phoneNumber is required" });

        var (isValid, validationError, normalizedPhone) = ValidateAndNormalizePhone(request.PhoneNumber);
        if (!isValid)
            return BadRequest(new { error = validationError });

        _logger.LogInformation("[PROVISION] ▶ Start | phone={Phone} user={UserId}",
            normalizedPhone, request.UserId);

        // ── 1. Get or Create phone record ──────────────────────────
        Phone phone;
        bool  isNew;
        try
        {
            if (request.UserId.HasValue)
            {
                (phone, isNew) = await _supabaseService.GetOrCreatePhoneAsync(
                    normalizedPhone!, request.UserId.Value, request.Nickname);
                _logger.LogInformation("[PROVISION] DB | phoneId={PhoneId} isNew={IsNew}",
                    phone.Id, isNew);
            }
            else
            {
                var existing = await _supabaseService.GetPhoneByNumberAsync(normalizedPhone!);
                if (existing != null)
                {
                    phone = existing; isNew = false;
                    _logger.LogInformation("[PROVISION] DB | found existing phoneId={PhoneId}", phone.Id);
                }
                else
                {
                    phone = await _supabaseService.CreatePhoneAsync(new Phone
                    {
                        Id = Guid.NewGuid(), Number = normalizedPhone!,
                        Label = request.Nickname, Color = request.Tag,
                        Status = "active", DockerStatus = PhoneDockerStatus.Pending,
                    });
                    isNew = true;
                    _logger.LogInformation("[PROVISION] DB | created new phoneId={PhoneId}", phone.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PROVISION] ✗ DB lookup/create failed | phone={Phone}", normalizedPhone);
            return StatusCode(500, new { error = "Database error: " + ex.Message });
        }

        // ── 2. Compute ports by GUID ────────────────────────────────
        var (fastApiPort, baileysPort) = PortHashCalculator.GetBothPorts(phone.Id, _configuration);
        _logger.LogInformation("[PROVISION] Ports | phoneId={PhoneId} fastApi={FastApi} baileys={Baileys}",
            phone.Id, fastApiPort, baileysPort);

        // ── 3. Update userId if missing ─────────────────────────────
        if (request.UserId.HasValue &&
            (phone.UserId == Guid.Empty || phone.UserId == null || phone.UserId != request.UserId))
        {
            _logger.LogInformation("[PROVISION] UserId | updating phoneId={PhoneId} userId={UserId}",
                phone.Id, request.UserId);
            await _supabaseService.UpdatePhoneUserIdAsync(phone.Id, request.UserId.Value);
            phone.UserId = request.UserId.Value;
        }

        // ── 4. Check if container already running ───────────────────
        bool containerRunning = false;
        try
        {
            containerRunning = !string.IsNullOrEmpty(phone.ContainerId)
                && await _dockerService.IsContainerRunningAsync(phone.ContainerId);
            _logger.LogInformation("[PROVISION] Container check | containerId={ContainerId} running={Running}",
                phone.ContainerId ?? "none", containerRunning);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PROVISION] Container check failed | containerId={ContainerId} — assuming not running",
                phone.ContainerId);
        }

        // ── 5. Start container if needed ────────────────────────────
        if (!containerRunning)
        {
            _logger.LogInformation("[PROVISION] Starting container | phoneId={PhoneId} isNew={IsNew}",
                phone.Id, isNew);
            bool started;
            try
            {
                started = await _containerManager.StartPhoneContainerAsync(phone);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PROVISION] ✗ StartPhoneContainerAsync threw | phoneId={PhoneId}", phone.Id);
                return StatusCode(500, new { error = "Container start exception: " + ex.Message });
            }

            if (!started)
            {
                _logger.LogError("[PROVISION] ✗ StartPhoneContainerAsync returned false | phoneId={PhoneId}", phone.Id);
                return StatusCode(500, new { error = "Failed to start container" });
            }

            _logger.LogInformation("[PROVISION] ✓ Container started | phoneId={PhoneId} — waiting 3s", phone.Id);
            await Task.Delay(3000);
        }
        else
        {
            _logger.LogInformation("[PROVISION] ↷ Skipping start — container already running | phoneId={PhoneId}", phone.Id);
        }

        // ── 6. Check WhatsApp status ────────────────────────────────
        string waStatus;
        try
        {
            waStatus = await GetContainerStatus(fastApiPort);
            _logger.LogInformation("[PROVISION] WA status | phoneId={PhoneId} port={Port} status={Status}",
                phone.Id, fastApiPort, waStatus);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PROVISION] WA status check failed | port={Port}", fastApiPort);
            waStatus = "unavailable";
        }

        if (waStatus == "connected")
        {
            _logger.LogInformation("[PROVISION] ✓ Done — connected | phoneId={PhoneId}", phone.Id);
            return Ok(new ProvisionResponse
            {
                PhoneId = phone.Id, PhoneNumber = normalizedPhone!,
                Label = phone.Label, Color = phone.Color,
                Port = fastApiPort, Status = "connected",
                Message = "Phone is already connected",
            });
        }

        // ── 7. Get QR ───────────────────────────────────────────────
        ContainerQrResponse? qrData = null;
        try
        {
            qrData = await GetContainerQr(fastApiPort);
            _logger.LogInformation("[PROVISION] QR | phoneId={PhoneId} hasQr={HasQr}",
                phone.Id, qrData?.Qr != null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PROVISION] QR fetch failed | port={Port}", fastApiPort);
        }

        _logger.LogInformation("[PROVISION] ✓ Done — qr_ready | phoneId={PhoneId}", phone.Id);
        return Ok(new ProvisionResponse
        {
            PhoneId = phone.Id, PhoneNumber = normalizedPhone!,
            Label = phone.Label, Color = phone.Color,
            Port = fastApiPort, Status = "qr_ready",
            QrCode = qrData?.Qr, QrImageBase64 = qrData?.QrImageBase64,
            QrRefreshUrl = $"/api/phones/{phone.Id}/qrcode",
            Message = "Scan the QR code to connect",
        });
    }

    [HttpGet("{id:guid}/qrcode")]
    public async Task<IActionResult> GetQrCode(Guid id)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(id);
        if (phone == null) return NotFound(new { error = "Phone not found" });

var (fastApiPort, baileysPort) = PortHashCalculator.GetBothPorts(phone.Id, _configuration);
        var waStatus = await GetContainerStatus(fastApiPort);

        if (waStatus == "connected")
            return Ok(new { status = "connected", message = "Phone is connected" });

        var qrData = await GetContainerQr(fastApiPort);
        if (qrData == null)
            return StatusCode(503, new { error = "Container not ready yet", status = waStatus });

        return Ok(new { status = "qr_ready", qr = qrData.Qr, qrImageBase64 = qrData.QrImageBase64 });
    }

    [HttpGet("{id:guid}/qrcode/image")]
    public async Task<IActionResult> GetQrCodeImage(Guid id)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(id);
        if (phone == null) return NotFound();

        var (fastApiPort, baileysPort) = PortHashCalculator.GetBothPorts(phone.Id, _configuration);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var bytes = await http.GetByteArrayAsync($"http://localhost:{fastApiPort}/qrcode/image");
            return File(bytes, "image/png");
        }
        catch { return StatusCode(503, new { error = "QR not available yet" }); }
    }

    [HttpPost("{phoneId}/pause")]
    public async Task<IActionResult> Pause(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null) return NotFound(new { error = "Phone not found" });

        var success = await _containerManager.PausePhoneContainerAsync(phone);
        if (!success) return StatusCode(500, new { error = "Failed to pause phone" });

        return Ok(new { success = true, message = "Phone paused.", resumeUrl = $"/api/phones/{phoneId}/resume" });
    }

    [HttpPost("{phoneId}/resume")]
    public async Task<IActionResult> Resume(Guid phoneId)
    {
        var phone = await _supabaseService.GetPhoneByIdAsync(phoneId);
        if (phone == null) return NotFound(new { error = "Phone not found" });

        var started = await _containerManager.StartPhoneContainerAsync(phone);
        if (!started) return StatusCode(500, new { error = "Failed to resume phone" });

        await Task.Delay(3000);
        var (fastApiPort, baileysPort) = PortHashCalculator.GetBothPorts(phone.Id, _configuration);
        var waStatus = await GetContainerStatus(fastApiPort);

        if (waStatus == "connected")
            return Ok(new { success = true, status = "connected", message = "Phone resumed and connected" });

        var qrData = await GetContainerQr(fastApiPort);
        return Ok(new { success = true, status = "qr_ready", message = "Phone resumed — scan QR to reconnect", qr = qrData?.Qr, qrImageBase64 = qrData?.QrImageBase64, qrRefreshUrl = $"/api/phones/{phoneId}/qrcode" });
    }

    // ── Private helpers ────────────────────────────────────────────────

    private static (bool isValid, string? error, string? normalized) ValidateAndNormalizePhone(string phone)
    {
        try
        {
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.Length < 7 || digits.Length > 15)  return (false, "Phone number must be between 7 and 15 digits", null);
            if (digits.StartsWith("0"))                    return (false, "Phone number must include country code without leading 0", null);
            if (digits.Distinct().Count() == 1)            return (false, "Invalid phone number", null);

            var phoneUtil = PhoneNumberUtil.GetInstance();
            var parsed    = phoneUtil.Parse("+" + digits, null);
            if (!phoneUtil.IsValidNumber(parsed))
                return (false, $"Invalid phone number for region {phoneUtil.GetRegionCodeForNumber(parsed)}", null);

            return (true, null, digits);
        }
        catch (NumberParseException) { return (false, "Could not parse phone number — include country code", null); }
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
}

// DTOs
public record ProvisionRequest
{
    public Guid?   UserId      { get; init; }
    public string  PhoneNumber { get; init; } = "";
    public string? Nickname    { get; init; }
    public string? Tag         { get; init; }
}

public record ProvisionResponse
{
    public Guid    PhoneId      { get; init; }
    public string  PhoneNumber  { get; init; } = "";
    public string? Label        { get; init; }
    public string? Color        { get; init; }
    public int     Port         { get; init; }
    public string  Status       { get; init; } = "";
    public string? QrCode       { get; init; }
    public string? QrImageBase64 { get; init; }
    public string? QrRefreshUrl { get; init; }
    public string  Message      { get; init; } = "";
}

record ContainerStatusResponse(string Status);
record ContainerQrResponse(string? Qr, string? QrImageBase64, string? Status);
