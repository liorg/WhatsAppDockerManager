using WhatsAppDockerManager.Configuration;
using WhatsAppDockerManager.Models;
using DbHost = WhatsAppDockerManager.Models.Host;
using Supabase;
namespace WhatsAppDockerManager.Services;

public interface IContainerManager
{
    Task InitializeAsync();
    Task<bool> StartPhoneContainerAsync(Phone phone);
    Task<bool> StopPhoneContainerAsync(Phone phone);
    Task<bool> RestartPhoneContainerAsync(Phone phone);
    Task SyncContainersAsync();
    Task HealthCheckAllAsync();
    Task TakeOverFromDeadHostAsync(Guid deadHostId);
    Guid? CurrentHostId { get; }
    Task<bool> PausePhoneContainerAsync(Phone phone);
}

public class ContainerManager : IContainerManager
{
    private readonly IDockerService _dockerService;
    private readonly ISupabaseService _supabaseService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ContainerManager> _logger;
    private readonly HostSettings _hostSettings;
    private readonly DockerSettings _dockerSettings;
    
    private DbHost? _currentHost;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public Guid? CurrentHostId => _currentHost?.Id;

    public ContainerManager(
        IDockerService dockerService,
        ISupabaseService supabaseService,
        IConfiguration configuration,
        ILogger<ContainerManager> logger)
    {
        _dockerService   = dockerService;
        _supabaseService = supabaseService;
        _configuration   = configuration;
        _logger          = logger;
        _hostSettings    = configuration.GetSection("AppSettings:Host").Get<HostSettings>() ?? new();
        _dockerSettings  = configuration.GetSection("AppSettings:Docker").Get<DockerSettings>() ?? new();
    }

    public async Task InitializeAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            _logger.LogInformation("Initializing Container Manager...");

            // ── זיהוי HostName אוטומטי אם לא מוגדר ─────────────
            var hostName = _hostSettings.HostName;
            if (string.IsNullOrEmpty(hostName))
            {
                hostName = System.Net.Dns.GetHostName();
                _logger.LogInformation("Detected host name: {HostName}", hostName);
            }

            // ── זיהוי IP מקומי אוטומטי אם לא מוגדר ─────────────
            var localIp = _hostSettings.IpAddress;
            if (string.IsNullOrEmpty(localIp) || localIp == "0.0.0.0")
            {
                try
                {
                    localIp = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName())
                        .AddressList
                        .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ?.ToString() ?? "0.0.0.0";
                    _logger.LogInformation("Detected local IP: {LocalIp}", localIp);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not detect local IP");
                }
            }

            // ── זיהוי IP חיצוני אוטומטי אם לא מוגדר ────────────
            var externalIp = _hostSettings.ExternalIp;
            if (string.IsNullOrEmpty(externalIp))
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    externalIp = (await http.GetStringAsync("http://checkip.amazonaws.com")).Trim();
                    //"http://checkip.amazonaws.com" https://api.ipify.org")
                    _logger.LogInformation("Detected external IP: {ExternalIp}", externalIp);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not detect external IP — using local IP as fallback");
                    externalIp = localIp;
                }
            }

            _currentHost = await _supabaseService.GetOrCreateHostAsync(
                hostName,
                localIp,
                externalIp,
                _hostSettings.PortRangeStart,
                _hostSettings.PortRangeEnd,
                _hostSettings.MaxContainers
            );

            if (_currentHost == null)
                throw new InvalidOperationException("Failed to register host in database");

            _logger.LogInformation("Host registered: {HostId} ({HostName})", _currentHost.Id, _currentHost.HostName);

            _logger.LogInformation("Ensuring Docker image is available: {Image}", _dockerSettings.ImageName);
           
            await _dockerService.EnsureNetworkExistsAsync("whatsapp_network"); // ← 
            await _dockerService.PullImageAsync(_dockerSettings.ImageName);

            await SyncContainersAsync();

            _initialized = true;
            _logger.LogInformation("Container Manager initialized successfully");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> StartPhoneContainerAsync(Phone phone)
    {
        if (_currentHost == null)
        {
            _logger.LogError("Host not initialized");
            return false;
        }

        try
        {
            _logger.LogInformation("Starting container for phone {PhoneNumber}", phone.Number);

            // ── הצב host_id אם חסר ──────────────────────────────
            if (phone.HostId == null)
            {
                _logger.LogInformation("Assigning phone {PhoneNumber} to host {HostId}", phone.Number, _currentHost.Id);
                await _supabaseService.AssignPhoneToHostAsync(phone.Id, _currentHost.Id);
                phone.HostId = _currentHost.Id;
            }

            await _supabaseService.UpdatePhoneDockerStatusAsync(phone.Id, PhoneDockerStatus.Starting);

            // ── חשב שני ports ────────────────────────────────────
            var (fastApiPort, baileysPort) = PortHashCalculator.GetBothPorts(phone.Number, _configuration);

            // ── שחזר creds אם קיים — בלי QR! ────────────────────
            if (!string.IsNullOrEmpty(phone.CredsBase64))
            {
                await RestoreCredsAsync(phone);
            }

            var containerId = await _dockerService.CreateAndStartContainerAsync(phone);

            if (containerId == null)
            {
                await _supabaseService.UpdatePhoneDockerStatusAsync(
                    phone.Id, PhoneDockerStatus.Error,
                    errorMessage: "Failed to create container");
                await _supabaseService.LogAgentEventAsync(
                    _currentHost.Id, AgentEventType.Error,
                    new { phoneId = phone.Id, error = "Failed to create container" });
                return false;
            }

            var host = !string.IsNullOrEmpty(_hostSettings.ExternalIp) ? _hostSettings.ExternalIp
                     : !string.IsNullOrEmpty(_hostSettings.IpAddress)  ? _hostSettings.IpAddress
                     : "localhost";
            var dockerUrl = $"http://{host}:{fastApiPort}";

            await _supabaseService.UpdatePhoneDockerStatusAsync(
                phone.Id,
                PhoneDockerStatus.Running,
                containerId:   containerId,
                containerName: $"whatsapp_{phone.Number.Replace("+", "")}",
                apiPort:       fastApiPort,
                dockerUrl:     dockerUrl);

            // ── רשום webhook ישירות ב-Baileys (port 3001) ────────
            await RegisterWebhookInContainerAsync(fastApiPort, phone.Id);

            // ── אם כבר connected — בקש resend-auth מ-Baileys ────
            await ReSendAuthIfConnectedAsync(fastApiPort, phone.Id);

            await _supabaseService.LogAgentEventAsync(
                _currentHost.Id, AgentEventType.Started,
                new { phoneId = phone.Id, containerId, fastApiPort, baileysPort, dockerUrl });

            _logger.LogInformation(
                "Container started for phone {PhoneNumber} FastAPI:{FastApi} Baileys:{Baileys}",
                phone.Number, fastApiPort, baileysPort);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting container for phone {PhoneNumber}", phone.Number);
            await _supabaseService.UpdatePhoneDockerStatusAsync(
                phone.Id, PhoneDockerStatus.Error, errorMessage: ex.Message);
            return false;
        }
    }

    /// <summary>
    /// שחזר creds.json מ-DB לפני הפעלת container — כך הוא יתחבר בלי QR
    /// </summary>
    private async Task RestoreCredsAsync(Phone phone)
    {
        try
        {
            var phoneIndex = phone.Number.Replace("+", "");  // ← כל המספר
            var authPath = Path.Combine(_dockerSettings.DataBasePath, $"auth_{phoneIndex}");

            Directory.CreateDirectory(authPath);

            var credsBytes = Convert.FromBase64String(phone.CredsBase64!);
            var credsPath  = Path.Combine(authPath, "creds.json");

            await File.WriteAllBytesAsync(credsPath, credsBytes);

            _logger.LogInformation(
                "Restored creds.json for phone {PhoneNumber} → {Path}",
                phone.Number, credsPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore creds for phone {PhoneNumber}", phone.Number);
        }
    }

    /// <summary>
    /// רשום webhook ישירות ב-Baileys (port 3001) — כך ה-authenticated event יגיע
    /// </summary>
private async Task RegisterWebhookInContainerAsync(int fastApiPort, Guid phoneId)
{
    try
    {
        // ← הגדל מ-3000 ל-8000
        await Task.Delay(8000);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        var host           = "172.17.0.1";
        var managerWebhook = $"http://{host}:5000/api/webhook/container-event/{phoneId}";
        var payload        = new { url = managerWebhook, secret = "manager-secret" };

        // ← נסה עד 3 פעמים
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var response = await httpClient.PostAsJsonAsync(
                    $"http://localhost:{fastApiPort}/webhooks/register", payload);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Webhook registered in Baileys for phone {PhoneId} on port {Port} (attempt {Attempt})",
                        phoneId, fastApiPort, attempt);
                    return;
                }

                _logger.LogWarning(
                    "Webhook registration attempt {Attempt} failed: {Status}",
                    attempt, response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Webhook registration attempt {Attempt} {fastApiPort} error: {Message} ",
                    attempt,fastApiPort, ex.Message);
            }

            if (attempt < 3)
                await Task.Delay(5000); // ← המתן 5 שניות בין ניסיונות
        }

        _logger.LogWarning("Could not register webhook after 3 attempts for  {fastApiPort} phone {PhoneId}", fastApiPort, phoneId);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Could not register webhook in fastApiPort {fastApiPort} for phone {PhoneId}", fastApiPort, phoneId);
    }
}

    /// <summary>
    /// אם ה-container כבר connected — בקש ממנו לשלוח שוב את ה-authenticated event עם creds
    /// </summary>
    private async Task ReSendAuthIfConnectedAsync(int baileysPort, Guid phoneId)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            var res = await http.GetFromJsonAsync<ContainerStatusResponse>(
                $"http://localhost:{baileysPort}/status");

            if (res?.Status == "connected")
            {
                _logger.LogInformation("Container already connected, requesting creds resend for {PhoneId}", phoneId);
                await http.PostAsync($"http://localhost:{baileysPort}/resend-auth", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resend auth for phone {PhoneId}", phoneId);
        }
    }

    public async Task<bool> StopPhoneContainerAsync(Phone phone)
    {
        if (string.IsNullOrEmpty(phone.ContainerId))
        {
            _logger.LogWarning("Phone {PhoneNumber} has no container ID", phone.Number);
            return false;
        }

        try
        {
            var success = await _dockerService.StopContainerAsync(phone.ContainerId);

            if (success)
            {
                await _supabaseService.UpdatePhoneDockerStatusAsync(phone.Id, PhoneDockerStatus.Stopped);
                await _supabaseService.LogAgentEventAsync(
                    _currentHost?.Id, AgentEventType.Stopped,
                    new { phoneId = phone.Id });
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping container for phone {PhoneNumber}", phone.Number);
            return false;
        }
    }

    public async Task<bool> RestartPhoneContainerAsync(Phone phone)
    {
        await StopPhoneContainerAsync(phone);

        if (!string.IsNullOrEmpty(phone.ContainerId))
            await _dockerService.RemoveContainerAsync(phone.ContainerId);

        return await StartPhoneContainerAsync(phone);
    }

    public async Task SyncContainersAsync()
    {
        if (_currentHost == null) return;

        await _syncLock.WaitAsync();
        try
        {
            _logger.LogInformation("Syncing containers with database...");

            var phones = await _supabaseService.GetPhonesForHostAsync(_currentHost.Id);

            var runningContainers = await _dockerService.ListContainersAsync(all: true);
            var runningContainerIds = runningContainers
                .Where(c => c.State == "running")
                .Select(c => c.ID)
                .ToHashSet();

            foreach (var phone in phones)
            {
                if (phone.DockerStatus == PhoneDockerStatus.Running &&
                    !string.IsNullOrEmpty(phone.ContainerId) &&
                    !runningContainerIds.Contains(phone.ContainerId))
                {
                    _logger.LogWarning("Container for phone {PhoneNumber} is not running, restarting...", phone.Number);
                    await RestartPhoneContainerAsync(phone);
                }
                else if (phone.DockerStatus == PhoneDockerStatus.Pending ||
                         phone.DockerStatus == PhoneDockerStatus.Unknown)
                {
                    _logger.LogInformation("Starting pending phone {PhoneNumber}", phone.Number);
                    await StartPhoneContainerAsync(phone);
                }
            }

            var orphanedPhones = await _supabaseService.GetOrphanedPhonesAsync();
            var currentCount   = phones.Count;

            foreach (var phone in orphanedPhones)
            {
                if (currentCount >= _hostSettings.MaxContainers)
                {
                    _logger.LogWarning("Host at capacity ({Max}), cannot claim more phones", _hostSettings.MaxContainers);
                    break;
                }

                _logger.LogInformation("Claiming orphaned phone {PhoneNumber}", phone.Number);
                await _supabaseService.AssignPhoneToHostAsync(phone.Id, _currentHost.Id);
                await StartPhoneContainerAsync(phone);
                currentCount++;
            }

            _logger.LogInformation("Container sync completed. Managing {Count} phones", currentCount);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task HealthCheckAllAsync()
    {
        if (_currentHost == null) return;

        try
        {
            var phones = await _supabaseService.GetPhonesForHostAsync(_currentHost.Id);

            foreach (var phone in phones.Where(p => p.DockerStatus == PhoneDockerStatus.Running))
            {
                if (string.IsNullOrEmpty(phone.ContainerId) || !phone.ApiPort.HasValue)
                    continue;

                var isHealthy = await _dockerService.CheckHealthAsync(phone.ContainerId, phone.ApiPort.Value);

                if (!isHealthy)
                {
                    _logger.LogWarning("Phone {PhoneNumber} failed health check", phone.Number);
                    await _supabaseService.LogAgentEventAsync(
                        _currentHost.Id, AgentEventType.HealthCheckFailed,
                        new { phoneId = phone.Id });
                    await RestartPhoneContainerAsync(phone);
                }
                else
                {
                    await _supabaseService.UpdatePhoneDockerStatusAsync(phone.Id, PhoneDockerStatus.Running);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during health check");
        }
    }

    public async Task TakeOverFromDeadHostAsync(Guid deadHostId)
    {
        if (_currentHost == null) return;

        try
        {
            _logger.LogWarning("Taking over phones from dead host {DeadHostId}", deadHostId);

            // ── קבל את כל הטלפונים של השרת המת ──────────────────
            var phones       = await _supabaseService.GetPhonesForHostAsync(deadHostId);
            var currentCount = (await _supabaseService.GetPhonesForHostAsync(_currentHost.Id)).Count;

            var takenOver  = new List<Guid>();
            var skipped    = new List<Guid>();
            var recovered  = new List<Guid>();

            foreach (var phone in phones)
            {
                if (currentCount >= _hostSettings.MaxContainers)
                {
                    _logger.LogWarning("Host at capacity ({Max}), cannot take over more phones", _hostSettings.MaxContainers);
                    skipped.Add(phone.Id);
                    continue;
                }

                try
                {
                    _logger.LogInformation(
                        "Taking over phone {PhoneNumber} from dead host {DeadHostId}",
                        phone.Number, deadHostId);

                    // ── הקצה לשרת הנוכחי ──────────────────────────
                    await _supabaseService.AssignPhoneToHostAsync(phone.Id, _currentHost.Id);

                    // ── אם יש creds — שחזר קובץ ועלה container ────
                    var hasCredentials = !string.IsNullOrEmpty(phone.CredsBase64);

                    if (hasCredentials)
                    {
                        _logger.LogInformation(
                            "Phone {PhoneNumber} has credentials — restoring and starting container",
                            phone.Number);
                        await RestoreCredsAsync(phone);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Phone {PhoneNumber} has no credentials — will need QR scan",
                            phone.Number);
                    }

                    // ── הפעל container ────────────────────────────
                    var started = await StartPhoneContainerAsync(phone);

                    if (started)
                    {
                        takenOver.Add(phone.Id);
                        recovered.Add(phone.Id);

                        // ── תעד במפורש מי לקח את מי ─────────────
                        await _supabaseService.LogAgentEventAsync(
                            _currentHost.Id,
                            AgentEventType.Migrated,
                            new
                            {
                                action          = "takeover",
                                phoneId         = phone.Id,
                                phoneNumber     = phone.Number,
                                fromHostId      = deadHostId,
                                toHostId        = _currentHost.Id,
                                hadCredentials  = hasCredentials,
                                restoredWithout = hasCredentials ? "QR" : "needs_QR",
                                timestamp       = DateTime.UtcNow
                            });

                        currentCount++;
                    }
                    else
                    {
                        _logger.LogError(
                            "Failed to start container for phone {PhoneNumber} during takeover",
                            phone.Number);
                    }
                }
                catch (Exception phoneEx)
                {
                    _logger.LogError(phoneEx,
                        "Error taking over phone {PhoneNumber}", phone.Number);
                }
            }

            // ── סמן את השרת המת כ-inactive ───────────────────────
            await _supabaseService.SetHostStatusAsync(deadHostId, "inactive");

            // ── תעד סיכום ה-takeover ──────────────────────────────
            await _supabaseService.LogAgentEventAsync(
                _currentHost.Id,
                AgentEventType.Migrated,
                new
                {
                    action       = "takeover_summary",
                    fromHostId   = deadHostId,
                    toHostId     = _currentHost.Id,
                    totalPhones  = phones.Count,
                    takenOver    = takenOver.Count,
                    skipped      = skipped.Count,
                    timestamp    = DateTime.UtcNow
                });

            _logger.LogInformation(
                "Takeover complete: {TakenOver}/{Total} phones from host {DeadHostId}",
                takenOver.Count, phones.Count, deadHostId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error taking over from dead host {DeadHostId}", deadHostId);
        }
    }
    // ב-ContainerManager — הוסף method:
public async Task<bool> PausePhoneContainerAsync(Phone phone)
{
    if (_currentHost == null)
    {
        _logger.LogError("Host not initialized");
        return false;
    }

    try
    {
        _logger.LogInformation("Pausing phone {PhoneNumber}", phone.Number);

        // 1. Stop container
        if (!string.IsNullOrEmpty(phone.ContainerId))
        {
            await _dockerService.StopContainerAsync(phone.ContainerId);
            _logger.LogInformation("Stopped container {ContainerId}", phone.ContainerId);
        }

        // 2. Remove container
        if (!string.IsNullOrEmpty(phone.ContainerId))
        {
            await _dockerService.RemoveContainerAsync(phone.ContainerId);
            _logger.LogInformation("Removed container {ContainerId}", phone.ContainerId);
        }

        // 3. מחק את כל הקבצים — auth + logs
        var phoneIndex = phone.Number.Replace("+", "");

        var authPath = Path.Combine(_dockerSettings.DataBasePath, $"auth_{phoneIndex}");
        if (Directory.Exists(authPath))
        {
            Directory.Delete(authPath, recursive: true);
            _logger.LogInformation("Deleted auth files at {Path}", authPath);
        }

        var logsPath = Path.Combine(_dockerSettings.DataBasePath, $"logs_{phoneIndex}");
        if (Directory.Exists(logsPath))
        {
            Directory.Delete(logsPath, recursive: true);
            _logger.LogInformation("Deleted logs at {Path}", logsPath);
        }
        // הוסף אחרי מחיקת logsPath
        var contactsPath = Path.Combine(_dockerSettings.DataBasePath, $"contacts_{phoneIndex}");
        if (Directory.Exists(contactsPath))
        {
            Directory.Delete(contactsPath, recursive: true);
            _logger.LogInformation("Deleted contacts files at {Path}", contactsPath);
        }

        // 4. נתק מה-host — null על host_id
        await _supabaseService.UpdatePhoneDockerStatusAsync(
            phone.Id,
            PhoneDockerStatus.Stopped,
            containerId:   "",
            containerName: "",
            dockerUrl:     "");

        await _supabaseService.DetachPhoneFromHostAsync(phone.Id);  // ← host_id = null

        await _supabaseService.LogAgentEventAsync(
            _currentHost.Id,
            AgentEventType.Stopped,
            new { phoneId = phone.Id, action = "pause", phoneNumber = phone.Number });

        _logger.LogInformation("Phone {PhoneNumber} paused and detached from host", phone.Number);
        return true;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error pausing phone {PhoneNumber}", phone.Number);
        await _supabaseService.UpdatePhoneDockerStatusAsync(
            phone.Id, PhoneDockerStatus.Error, errorMessage: ex.Message);
        return false;
    }
}


}

// DTO לבדיקת status
record ContainerStatusResponse(string Status);