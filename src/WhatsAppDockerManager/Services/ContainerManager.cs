using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
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
    string? CurrentImageDigest { get; }
}

public class ContainerManager : IContainerManager
{
    private readonly IDockerService _dockerService;
    private readonly IImageCacheService _imageCache;
    private readonly ISupabaseService _supabaseService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ContainerManager> _logger;
    private readonly HostSettings _hostSettings;
    private readonly DockerSettings _dockerSettings;

    private DbHost? _currentHost;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private const string ProviderCloudApi = "cloudapi";

    // ── פרפורמנס ────────────────────────────────────────────────────
    // HttpClient אחד משותף — במקום new HttpClient בכל קריאה (socket exhaustion)
    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout           = TimeSpan.FromSeconds(2),
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    // כמה containers עולים במקביל (Sync / HealthCheck / TakeOver)
    private readonly int _maxParallelStarts;
    // זמן מקסימלי להמתנה ל-container מוכן (במקום Task.Delay(8000) קבוע)
    private readonly int _readyTimeoutSeconds;

    // שמירת פורטים בזיכרון — מונע התנגשות פורטים כשמעלים במקביל
    private readonly SemaphoreSlim _portLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, (int Fa, int Ba)> _portReservations = new();

    public Guid?   CurrentHostId      => _currentHost?.Id;
    public string? CurrentImageDigest { get; private set; }
    private DateTime? _currentImageCreated;

    public ContainerManager(
        IDockerService dockerService,
        IImageCacheService imageCache,
        ISupabaseService supabaseService,
        IConfiguration configuration,
        ILogger<ContainerManager> logger)
    {
        _dockerService   = dockerService;
        _imageCache      = imageCache;
        _supabaseService = supabaseService;
        _configuration   = configuration;
        _logger          = logger;
        _hostSettings    = configuration.GetSection("AppSettings:Host").Get<HostSettings>() ?? new();
        _dockerSettings  = configuration.GetSection("AppSettings:Docker").Get<DockerSettings>() ?? new();

        _maxParallelStarts   = Math.Max(1, configuration.GetValue<int?>("AppSettings:Docker:MaxParallelStarts") ?? 4);
        _readyTimeoutSeconds = Math.Max(5, configuration.GetValue<int?>("AppSettings:Docker:ReadyTimeoutSeconds") ?? 45);
    }

    public async Task InitializeAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            _logger.LogInformation("[CONTAINER] Initializing Container Manager...");

            var hostName = _hostSettings.HostName;
            if (string.IsNullOrEmpty(hostName))
            {
                hostName = System.Net.Dns.GetHostName();
                _logger.LogInformation("[CONTAINER] Detected host name: {HostName}", hostName);
            }

            var localIp = _hostSettings.IpAddress;
            if (string.IsNullOrEmpty(localIp) || localIp == "0.0.0.0")
            {
                try
                {
                    localIp = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName())
                        .AddressList
                        .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ?.ToString() ?? "0.0.0.0";
                    _logger.LogInformation("[CONTAINER] Detected local IP: {LocalIp}", localIp);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CONTAINER] Could not detect local IP");
                }
            }

            var externalIp = _hostSettings.ExternalIp;
            if (string.IsNullOrEmpty(externalIp))
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    externalIp = (await _http.GetStringAsync("http://checkip.amazonaws.com", cts.Token)).Trim();
                    _logger.LogInformation("[CONTAINER] Detected external IP: {ExternalIp}", externalIp);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CONTAINER] Could not detect external IP — using local IP as fallback");
                    externalIp = localIp;
                }
            }

            _currentHost = await _supabaseService.GetOrCreateHostAsync(
                hostName, localIp, externalIp,
                _hostSettings.PortRangeStart, _hostSettings.PortRangeEnd, _hostSettings.MaxContainers);

            if (_currentHost == null)
                throw new InvalidOperationException("[CONTAINER] Failed to register host in database");

            _logger.LogInformation("[CONTAINER] Host registered: {HostId} ({HostName})", _currentHost.Id, _currentHost.HostName);

            await _dockerService.EnsureNetworkExistsAsync("whatsapp_network");
            await _dockerService.EnsureRedisContainerRunningAsync();

            // ── Images: לפי טבלת providers. משתמשים ב-cache מיד;
            //    pull חוסם רק ל-image שחסר. העדכון השוטף — ImageCacheService (ברקע) ────
            //    כאן אין שום התייחסות ל-cloudapi: הוספת שורה בטבלת providers עם
            //    liorgr/whatsapp-cloudapi:latest מספיקה כדי שה-image ייכנס ל-cache.
            var imgSw  = Stopwatch.StartNew();
            var images = await _supabaseService.GetProviderImagesAsync();
            var cached = await Task.WhenAll(images.Select(i => _imageCache.EnsureCachedAsync(i)));

            for (var i = 0; i < images.Count; i++)
                if (!cached[i])
                    _logger.LogError("[CONTAINER] ❌ Image {Image} not available (no cache, pull failed)", images[i]);

            var mainImage = images.FirstOrDefault(i => i.StartsWith(_dockerSettings.ImageName.Split(':')[0] + ":"))
                            ?? images.FirstOrDefault();
            var imageInfo = mainImage != null ? await _imageCache.GetInfoAsync(mainImage) : null;
            if (imageInfo != null)
            {
                CurrentImageDigest   = imageInfo.Id;
                _currentImageCreated = imageInfo.Created;
            }
            _logger.LogInformation("[CONTAINER] 📦 {Count} provider images ready in {Ms}ms: {Images}",
                cached.Count(c => c), imgSw.ElapsedMilliseconds, string.Join(", ", images));

            await SyncContainersAsync();

            // Sync מטפל רק במה שלא רץ. טלפון שרץ ו-Redis שנמחק מתחתיו הוא
            // בדיוק המקרה שהוא מדלג עליו, ולכן סבב webhooks נפרד כאן.
            await EnsureAllWebhooksAsync();

            // ── pull אחד ברקע אחרי שה-containers עלו מה-cache — מעדכן ל-restart הבא ──
            _ = Task.Run(async () =>
            {
                try { await _imageCache.PrepullAllAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "[CONTAINER] Background prepull failed"); }
            });

            _initialized = true;
            _logger.LogInformation("[CONTAINER] Container Manager initialized successfully");
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
            _logger.LogError("[CONTAINER] Host not initialized");
            return false;
        }

        var hostId        = _currentHost.Id;
        var sw            = Stopwatch.StartNew();
        var t             = new StepTimer();
        var containerName = PhonePathHelper.ContainerName(phone.Number, phone.Id);
        string? containerId = null;
        string? dockerUrl   = null;
        int fastApiPort = 0, baileysPort = 0;
        int revision    = 0;

        try
        {
            _logger.LogInformation("[CONTAINER] Starting container for phone {PhoneNumber}", phone.Number);

            // ── RPC 1: host + Starting + revision+1 + ports + masked user ──
            var prep = await _supabaseService.PrepareStartAsync(phone.Id, hostId, PhoneDockerStatus.Starting);
            t.Mark("prepare_rpc");

            revision           = prep.Revision;
            phone.HostId       = prep.HostId;
            phone.AuthRevision = revision;

            var isCloud = string.Equals(prep.Provider, ProviderCloudApi, StringComparison.OrdinalIgnoreCase);

            // image לפי provider — מה-cache (pull רק אם חסר)
            if (!await _imageCache.EnsureCachedAsync(prep.Image))
                throw new InvalidOperationException($"Image {prep.Image} not available");
            t.Mark("image");

            // ── המזהים של מטא. מגיעים מ-prepare אם ה-RPC כבר מחזיר אותם;
            //    אחרת שליפה נפרדת — רק במסלול cloudapi, לא בנתיב החם של baileys.
            CloudApiSpec? cloud = null;
            if (isCloud)
            {
                cloud = await ResolveCloudSpecAsync(phone.Id, prep);
                t.Mark("cloud_spec");
            }

            // ל-cloudapi אין session מקומי — אין מה לשחזר
            if (!isCloud && !string.IsNullOrEmpty(phone.CredsBase64))
                await RestoreCredsAsync(phone);
            t.Mark("creds");

            (fastApiPort, baileysPort) = await ReservePortsAsync(phone.Id, prep.UsedApiPorts, prep.UsedWsPorts);
            t.Mark("ports");

            _logger.LogInformation("[CONTAINER] provider={Provider} image={Image} revision={Rev} user={User} prep={Ms}ms",
                prep.Provider, prep.Image, revision, prep.MaskedUser, sw.ElapsedMilliseconds);

            containerId = await _dockerService.CreateAndStartContainerAsync(
                phone, fastApiPort, baileysPort, revision, prep.MaskedUser, prep.Image, prep.Provider, cloud);
            t.Mark("docker_create");

            if (containerId == null)
            {
                _portReservations.TryRemove(phone.Id, out _);
                await _supabaseService.FinishStartAsync(phone.Id, revision, PhoneDockerStatus.Error,
                    errorMessage: "Failed to create container");
                return false;
            }

            var host = !string.IsNullOrEmpty(_hostSettings.ExternalIp) ? _hostSettings.ExternalIp
                     : !string.IsNullOrEmpty(_hostSettings.IpAddress)  ? _hostSettings.IpAddress
                     : "localhost";
            dockerUrl = $"http://{host}:{fastApiPort}";

            phone.ContainerId = containerId;
            phone.ApiPort     = fastApiPort;
            phone.WsPort      = isCloud ? null : baileysPort;

            // ── ממתינים לסיום אמיתי ──────────────────────────────────────
            // מסלול אחד לשני הספקים: ready → webhook → resend-auth.
            // ההבדל היחיד הוא בדיקת החיבור ל-NATS, שרלוונטית רק ל-cloudapi.
            var (ok, error) = await PostStartAsync(phone.Id, fastApiPort, t, isCloud);

            // ── RPC 2: סטטוס סופי — רק אם ה-revision עדיין שלנו ──────────
            var applied = await _supabaseService.FinishStartAsync(phone.Id, revision,
                ok ? PhoneDockerStatus.Running : PhoneDockerStatus.Error,
                containerId, containerName, fastApiPort, isCloud ? null : baileysPort, dockerUrl,
                errorMessage: error);
            t.Mark("finish_rpc");
            _logger.LogInformation("[TIMING] phone={PhoneNumber} provider={Provider} ok={Ok} {Steps}",
                phone.Number, prep.Provider, ok, t);

            if (!applied)
            {
                // Start חדש יותר של אותו טלפון כבר רץ — ה-container שלנו מיותר
                _logger.LogWarning("[CONTAINER] Phone {PhoneNumber} rev={Rev} superseded — removing {ContainerId}",
                    phone.Number, revision, containerId);
                await RemoveOwnContainerAsync(phone.Id, containerId);
                return false;
            }

            if (!ok)
                await RemoveOwnContainerAsync(phone.Id, containerId);   // לא משאירים container חצי-חי עם revision תקף

            if (ok)
                _logger.LogInformation("[CONTAINER] ✅ Phone {PhoneNumber} READY ({What}) FastAPI:{FastApi} in {Ms}ms",
                    phone.Number, isCloud ? "registered with whqueue" : "webhook registered",
                    fastApiPort, sw.ElapsedMilliseconds);
            else
                _logger.LogError("[CONTAINER] ❌ Phone {PhoneNumber} start FAILED after {Ms}ms: {Error}",
                    phone.Number, sw.ElapsedMilliseconds, error);

            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CONTAINER] ❌ Error starting container for phone {PhoneNumber} after {Ms}ms",
                phone.Number, sw.ElapsedMilliseconds);
            _logger.LogInformation("[TIMING] phone={PhoneNumber} ok=False (exception) {Steps}", phone.Number, t);
            if (containerId != null)
                await RemoveOwnContainerAsync(phone.Id, containerId);

            try
            {
                if (revision > 0)   // prepare הצליח → יש revision לגדר
                    await _supabaseService.FinishStartAsync(phone.Id, revision, PhoneDockerStatus.Error, errorMessage: ex.Message);
                else
                    await _supabaseService.UpdatePhoneDockerStatusAsync(phone.Id, PhoneDockerStatus.Error, errorMessage: ex.Message);
            }
            catch (Exception rpcEx)
            {
                _logger.LogError(rpcEx, "[CONTAINER] start_phone_finish failed for {PhoneId}", phone.Id);
            }
            return false;
        }
    }

    /// <summary>
    /// המזהים של Cloud API. אם start_phone_prepare כבר מחזיר אותם — אפס round trips.
    /// אחרת שליפה נפרדת, שעולה רק בטלפוני cloudapi.
    /// </summary>
    private async Task<CloudApiSpec> ResolveCloudSpecAsync(Guid phoneId, StartPrepareResult prep)
    {
        if (!string.IsNullOrWhiteSpace(prep.PhoneNumberId))
            return new CloudApiSpec
            {
                WabaId        = prep.WabaId ?? "",
                PhoneNumberId = prep.PhoneNumberId!,
                AccessToken   = prep.CloudAccessToken ?? "",
                VerifyToken   = prep.CloudVerifyToken,
            };

        var spec = await _supabaseService.GetCloudApiSpecAsync(phoneId);
        if (spec == null || string.IsNullOrWhiteSpace(spec.PhoneNumberId))
            throw new InvalidOperationException(
                $"phone {phoneId} is provider=cloudapi but has no phone_number_id in phones");
        return spec;
    }

    private async Task RemoveOwnContainerAsync(Guid phoneId, string? containerId)
    {
        _portReservations.TryRemove(phoneId, out _);
        if (string.IsNullOrEmpty(containerId)) return;
        try
        {
            await _dockerService.RemoveContainerAsync(containerId);   // Force — בלי המתנת stop
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CONTAINER] Failed removing container {ContainerId}", containerId);
        }
    }

    /// <summary>
    /// הקצאת פורטים תחת נעילה + שמירה בזיכרון, כך ש-starts מקביליים
    /// לא יקבלו את אותו פורט לפני שה-DB התעדכן.
    /// </summary>
    private async Task<(int Fa, int Ba)> ReservePortsAsync(Guid phoneId, IEnumerable<int> dbUsedFa, IEnumerable<int> dbUsedBa)
    {
        await _portLock.WaitAsync();
        try
        {
            var reservedOthers = _portReservations.Where(kv => kv.Key != phoneId).Select(kv => kv.Value).ToList();
            var usedFa = dbUsedFa.Concat(reservedOthers.Select(r => r.Fa)).ToHashSet();
            var usedBa = dbUsedBa.Concat(reservedOthers.Select(r => r.Ba)).ToHashSet();

            var ports = PortHashCalculator.GetBothPortsUnique(phoneId, usedFa, usedBa, _configuration);
            _portReservations[phoneId] = ports;
            return ports;
        }
        finally
        {
            _portLock.Release();
        }
    }

    private async Task RestoreCredsAsync(Phone phone)
    {
        try
        {
            var authPath = PhonePathHelper.AuthPath(_dockerSettings.DataBasePath, phone.Id);
            Directory.CreateDirectory(authPath);
            var credsBytes = Convert.FromBase64String(phone.CredsBase64!);
            var credsPath  = Path.Combine(authPath, "creds.json");
            await File.WriteAllBytesAsync(credsPath, credsBytes);
            _logger.LogInformation("Restored creds.json for phone {PhoneNumber} → {Path}", phone.Number, credsPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore creds for phone {PhoneNumber}", phone.Number);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Post-start: readiness polling → webhook → resend-auth   (baileys)
    // ════════════════════════════════════════════════════════════════
    private async Task<(bool Ok, string? Error)> PostStartAsync(Guid phoneId, int fastApiPort,
                                                                StepTimer t, bool isCloud)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (ready, attempts) = await WaitForContainerReadyAsync(fastApiPort, TimeSpan.FromSeconds(_readyTimeoutSeconds));
            t.Mark($"wait_ready({attempts}x)");
            if (!ready)
                return (false, $"Container not ready after {_readyTimeoutSeconds}s");

            _logger.LogInformation("[CONTAINER] Phone {PhoneId} ready in {Ms}ms ({Attempts} polls)", phoneId, sw.ElapsedMilliseconds, attempts);

            var registered = await RegisterWebhookInContainerAsync(fastApiPort, phoneId);
            t.Mark("webhook");
            if (!registered)
                return (false, "Webhook registration failed");

            // בדיקה אחרי הרישום: אם כבר connected — אירוע ה-creds כבר עבר בלי webhook → resend.
            // אם יתחבר אחרי הרישום — האירוע יגיע כרגיל.
            await ReSendAuthIfConnectedAsync(fastApiPort, phoneId);
            t.Mark("resend_auth");

            // ── cloudapi: הצרכן חייב להיות מחובר ─────────────────────────
            // קונטיינר בלי חיבור ל-NATS הוא טלפון שלא מקבל הודעות נכנסות,
            // ונראה בריא לחלוטין. זה הכשל השקט היחיד בארכיטקטורה הזו, ולכן
            // הוא נבדק במפורש ומפיל את ה-start.
            if (isCloud)
            {
                var (inbound, why) = await WaitForInboundReadyAsync(phoneId, fastApiPort);
                t.Mark("inbound_ready");
                if (!inbound)
                    return (false, why);
            }

            _logger.LogInformation("[CONTAINER] Post-start done for {PhoneId} in {Ms}ms", phoneId, sw.ElapsedMilliseconds);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CONTAINER] Post-start failed for phone {PhoneId}", phoneId);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// ממתין שהקונטיינר יאמת שהוא מחובר ל-NATS ושהטוקן של מטא תקף.
    /// </summary>
    private async Task<(bool Ok, string? Error)> WaitForInboundReadyAsync(Guid phoneId, int fastApiPort)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(_readyTimeoutSeconds);
        string? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var h = await _http.GetFromJsonAsync<CloudHealthResponse>(
                    $"http://localhost:{fastApiPort}/health", cts.Token);

                if (h is { NatsConnected: true, TokenOk: true })
                {
                    _logger.LogInformation("[CONTAINER] Phone {PhoneId} inbound live (nats connected)", phoneId);
                    return (true, null);
                }

                if (h != null)
                    last = h.TokenOk
                        ? $"nats not connected ({h.NatsError})"
                        : "Cloud API token rejected by Graph — update phones.cloud_access_token";
            }
            catch (Exception ex) { last = ex.Message; }

            await Task.Delay(300);
        }

        return (false, $"Inbound not ready after {_readyTimeoutSeconds}s ({last})");
    }

    private async Task<(bool Ready, int Attempts)> WaitForContainerReadyAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        const int delayMs = 200;   // polling קבוע — לא מפספסים את רגע ה-ready
        var attempts = 0;

        while (DateTime.UtcNow < deadline)
        {
            attempts++;
            try
            {
                using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var resp = await _http.GetAsync($"http://localhost:{port}/status", cts.Token);
                if (resp.IsSuccessStatusCode) return (true, attempts);
            }
            catch { /* עדיין עולה */ }

            await Task.Delay(delayMs);
        }
        return (false, attempts);
    }

    /// <summary>
    /// מוודא שיש webhook רשום, ורושם מחדש אם אין. **לשני הספקים.**
    ///
    /// הרישום יושב ב-redis_shared, שהוא חיצוני לקונטיינרים — וזו גם החשיפה:
    /// מחיקה של redis_shared, או eviction, מרוקנת אותו בעוד הקונטיינרים
    /// ממשיכים לרוץ. ו-SyncContainersAsync **מדלג** על טלפון שהקונטיינר שלו
    /// רץ ו-DockerStatus=Running, ולכן אתחול ה-Manager לבדו לא היה מחזיר את
    /// הרישום. הטלפון נראה בריא לחלוטין ולא מקבל הודעות.
    ///
    /// נבדק דרך GET /webhooks ולא דרך /health, כי זה ה-endpoint היחיד ששני
    /// ה-images מחזירים באותו מבנה — ב-baileys אין webhooks_count ב-/health
    /// בכלל, והוא דווקא הספק שאין לו שום התאוששות משל עצמו: registerWebhook
    /// נקרא רק מה-Manager, רק בעת start.
    /// </summary>
    private async Task EnsureWebhookRegisteredAsync(Phone phone)
    {
        if (!phone.ApiPort.HasValue) return;

        try
        {
            var list = await _http.GetFromJsonAsync<WebhookListResponse>(
                $"http://localhost:{phone.ApiPort.Value}/webhooks");

            var mine = list?.Webhooks?.Count(w => w.Contains(phone.Id.ToString())) ?? 0;
            if (mine > 0) return;

            // cloudapi מצרף את מצב Redis לאותה תשובה. ריק בגלל Redis שלא נגיש
            // הוא "לא ידוע", לא "אין רישום" — ורישום מחדש היה נכשל ב-503 וממלא
            // את הלוג בכל סבב health check.
            if (list?.Redis?.Enabled == false || list?.Redis?.Error is { Length: > 0 })
            {
                _logger.LogError(
                    "[CONTAINER] ✗ phone {PhoneNumber}: redis unavailable ({Err}) — webhook state unknown",
                    phone.Number, list.Redis.Error ?? "not configured");
                return;
            }

            _logger.LogWarning(
                "[CONTAINER] ⚠ phone {PhoneNumber} ({Provider}) has no webhook{Key} — re-registering",
                phone.Number, phone.Provider ?? "baileys",
                list?.Key is { Length: > 0 } k ? $" in {k}" : "");

            var ok = await RegisterWebhookInContainerAsync(phone.ApiPort.Value, phone.Id);
            if (ok)
                _logger.LogInformation("[CONTAINER] ✓ webhook restored for {PhoneNumber}", phone.Number);
            else
                _logger.LogError("[CONTAINER] ✗ could not restore webhook for {PhoneNumber} — phone is mute", phone.Number);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[CONTAINER] webhook check failed for {PhoneNumber}: {Msg}", phone.Number, ex.Message);
        }
    }

    /// <summary>
    /// סבב בדיקה על כל הטלפונים שרצים. נקרא אחרי SyncContainersAsync בעלייה,
    /// כדי שמחיקה של redis_shared תתוקן ברגע שה-Manager עולה — ולא רק בסבב
    /// ה-health check הבא.
    /// </summary>
    private async Task EnsureAllWebhooksAsync()
    {
        if (_currentHost == null) return;
        try
        {
            var phones = (await _supabaseService.GetPhonesForHostAsync(_currentHost.Id))
                .Where(p => p.DockerStatus == PhoneDockerStatus.Running && p.ApiPort.HasValue)
                .ToList();

            if (phones.Count == 0) return;

            await RunThrottledAsync(phones
                .Select(p => (Func<Task>)(() => EnsureWebhookRegisteredAsync(p)))
                .ToList());

            _logger.LogInformation("[CONTAINER] webhook sweep done over {Count} running phones", phones.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CONTAINER] webhook sweep failed");
        }
    }

    private async Task<bool> RegisterWebhookInContainerAsync(int fastApiPort, Guid phoneId)
    {
        var baseUrl = $"http://localhost:{fastApiPort}";

        // מ-config ולא קבוע בקוד. DockerService מזריק MANAGER_WEBHOOK_URL מאותם
        // שני מפתחות, והקונטיינר זורע אותו ל-Redis בעלייה. אם הערכים לא זהים
        // נוצרות **שתי** רשומות שונות ב-SET — והדה-דופליקציה לא תעזור, כי מבחינתה
        // אלה שני יעדים שונים. ה-rstrip חשוב מאותה סיבה: ManagerUrl עם לוכסן
        // עוקב היה מייצר "…:5000//api/…".
        var managerUrl     = (_configuration["AppSettings:ManagerUrl"] is { Length: > 0 } mu
                                ? mu : "http://172.17.0.1:5000").TrimEnd('/');
        var secret         = _configuration["AppSettings:WebhookSecret"] is { Length: > 0 } ws
                                ? ws : "manager-secret";
        var managerWebhook = $"{managerUrl}/api/webhook/container-event/{phoneId}";
        var payload        = new { url = managerWebhook, secret };

        try
        {
            var list  = await _http.GetFromJsonAsync<WebhookListResponse>($"{baseUrl}/webhooks");
            var stale = list?.Webhooks?
                .Where(wh => wh.Contains("container-event") && !wh.Contains(phoneId.ToString()))
                .ToList() ?? new List<string>();

            await Task.WhenAll(stale.Select(async wh =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/webhooks/unregister")
                    {
                        Content = JsonContent.Create(new { url = wh })
                    };
                    using var _ = await _http.SendAsync(req);
                    _logger.LogInformation("Unregistered stale webhook: {Url}", wh);
                }
                catch { }
            }));
        }
        catch (Exception ex) { _logger.LogWarning("Could not clean webhooks: {Msg}", ex.Message); }

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync($"{baseUrl}/webhooks/register", payload);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Webhook registered for phone {PhoneId} port {Port} (attempt {Attempt})",
                        phoneId, fastApiPort, attempt);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Webhook registration attempt {Attempt} error: {Message}", attempt, ex.Message);
            }
            await Task.Delay(300 * attempt);
        }

        _logger.LogWarning("Could not register webhook for phone {PhoneId}", phoneId);
        return false;
    }

    private async Task ReSendAuthIfConnectedAsync(int fastApiPort, Guid phoneId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var res = await _http.GetFromJsonAsync<ContainerStatusResponse>(
                $"http://localhost:{fastApiPort}/status", cts.Token);
            if (res?.Status == "connected")
            {
                _logger.LogInformation("[CONTAINER] Container already connected, requesting creds resend for {PhoneId}", phoneId);
                using var _ = await _http.PostAsync($"http://localhost:{fastApiPort}/resend-auth", null, cts.Token);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[CONTAINER] Could not resend auth for phone {PhoneId}", phoneId); }
    }

    public async Task<bool> StopPhoneContainerAsync(Phone phone)
    {
        if (string.IsNullOrEmpty(phone.ContainerId)) { _logger.LogWarning("[CONTAINER] Phone {PhoneNumber} has no container ID", phone.Number); return false; }
        try
        {
            var success = await _dockerService.StopContainerAsync(phone.ContainerId);
            if (success)
            {
                await _supabaseService.UpdatePhoneDockerStatusAsync(phone.Id, PhoneDockerStatus.Stopped);
            }
            return success;
        }
        catch (Exception ex) { _logger.LogError(ex, "[CONTAINER] Error stopping container for phone {PhoneNumber}", phone.Number); return false; }
    }

    public async Task<bool> RestartPhoneContainerAsync(Phone phone)
    {
        _logger.LogInformation("[CONTAINER] Restarting phone {PhoneNumber} (id={PhoneId})", phone.Number, phone.Id);
        var rsw = Stopwatch.StartNew();

        if (!string.IsNullOrEmpty(phone.ContainerId))
        {
            await _dockerService.StopContainerAsync(phone.ContainerId);
            await _dockerService.RemoveContainerAsync(phone.ContainerId);
        }
        else
        {
            var expectedName = PhonePathHelper.ContainerName(phone.Number, phone.Id);
            _logger.LogWarning("[CONTAINER] No containerId in DB — trying by name: {Name}", expectedName);
            await _dockerService.RemoveContainerByNameAsync(expectedName);
        }

        phone.ContainerId = null;
        _logger.LogInformation("[TIMING] phone={PhoneNumber} restart_stop_remove={Ms}ms", phone.Number, rsw.ElapsedMilliseconds);

        var result = await StartPhoneContainerAsync(phone);
        _logger.LogInformation("[CONTAINER] Restart {Result} for phone {PhoneNumber}",
            result ? "✓ succeeded" : "✗ failed", phone.Number);
        return result;
    }

    public async Task SyncContainersAsync()
    {
        if (_currentHost == null) return;
        var hostId = _currentHost.Id;

        await _syncLock.WaitAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            _logger.LogInformation("[CONTAINER] Syncing containers with database...");

            var phonesTask     = _supabaseService.GetPhonesForHostAsync(hostId);
            var containersTask = _dockerService.ListContainersAsync(all: true);
            var orphansTask    = _supabaseService.GetOrphanedPhonesAsync();
            await Task.WhenAll(phonesTask, containersTask, orphansTask);

            var phones              = phonesTask.Result;
            var runningContainerIds = containersTask.Result.Where(c => c.State == "running").Select(c => c.ID).ToHashSet();

            var jobs = new List<Func<Task>>();
            var ok   = 0;
            var fail = 0;
            void Count(bool r) { if (r) Interlocked.Increment(ref ok); else Interlocked.Increment(ref fail); }

            foreach (var phone in phones)
            {
                if (phone.DockerStatus == PhoneDockerStatus.Running && !string.IsNullOrEmpty(phone.ContainerId) && !runningContainerIds.Contains(phone.ContainerId))
                {
                    _logger.LogWarning("[CONTAINER] Container for phone {PhoneNumber} is not running, restarting...", phone.Number);
                    jobs.Add(async () => Count(await RestartPhoneContainerAsync(phone)));
                }
                else if (phone.DockerStatus == PhoneDockerStatus.Pending || phone.DockerStatus == PhoneDockerStatus.Unknown)
                {
                    _logger.LogInformation("[CONTAINER] Starting pending phone {PhoneNumber}", phone.Number);
                    jobs.Add(async () => Count(await StartPhoneContainerAsync(phone)));
                }
            }

            var currentCount = phones.Count;
            foreach (var orphan in orphansTask.Result)
            {
                if (currentCount >= _hostSettings.MaxContainers)
                {
                    _logger.LogWarning("[CONTAINER] Host at capacity ({Max}), cannot claim more phones", _hostSettings.MaxContainers);
                    break;
                }
                _logger.LogInformation("[CONTAINER] Claiming orphaned phone {PhoneNumber}", orphan.Number);
                jobs.Add(async () => Count(await StartPhoneContainerAsync(orphan)));
                currentCount++;
            }

            await RunThrottledAsync(jobs);

            _logger.LogInformation("[CONTAINER] ✅ Sync completed: {Ok} ready, {Fail} failed, {Count} managed, in {Ms}ms",
                ok, fail, currentCount, sw.ElapsedMilliseconds);
        }
        finally { _syncLock.Release(); }
    }

    public async Task HealthCheckAllAsync()
    {
        if (_currentHost == null) return;
        var hostId = _currentHost.Id;
        try
        {
            var phones = await _supabaseService.GetPhonesForHostAsync(hostId);

            var jobs = phones
                .Where(p => p.DockerStatus == PhoneDockerStatus.Running && !string.IsNullOrEmpty(p.ContainerId) && p.ApiPort.HasValue)
                .Select(phone => (Func<Task>)(async () =>
                {
                    var isHealthy = await _dockerService.CheckHealthAsync(phone.ContainerId!, phone.ApiPort!.Value);
                    if (!isHealthy)
                    {
                        _logger.LogWarning("[CONTAINER] Phone {PhoneNumber} failed health check", phone.Number);
                        await RestartPhoneContainerAsync(phone);
                    }
                    else
                    {
                        // ── לאמת שה-webhook עוד רשום ─────────────────────────
                        // שני הספקים, כי הרישום של שניהם יושב באותו Redis
                        // חיצוני — ומה שמרוקן אותו מרוקן אותו לשניהם.
                        // קונטיינר בלי webhook מחזיר /health = 200 ונראה בריא
                        // לחלוטין, וההודעות חוזרות לתור לנצח בלי שאף תרחיש
                        // יופעל. זו הבדיקה שהופכת את זה לכשל שמתקן את עצמו.
                        await EnsureWebhookRegisteredAsync(phone);

                        await _supabaseService.UpdatePhoneDockerStatusAsync(phone.Id, PhoneDockerStatus.Running);
                    }
                }))
                .ToList();

            await RunThrottledAsync(jobs);
        }
        catch (Exception ex) { _logger.LogError(ex, "[CONTAINER] Error during health check"); }
    }

    public async Task TakeOverFromDeadHostAsync(Guid deadHostId)
    {
        if (_currentHost == null) return;
        var hostId = _currentHost.Id;
        try
        {
            _logger.LogWarning("[CONTAINER] Taking over phones from dead host {DeadHostId}", deadHostId);

            var deadTask = _supabaseService.GetPhonesForHostAsync(deadHostId);
            var mineTask = _supabaseService.GetPhonesForHostAsync(hostId);
            await Task.WhenAll(deadTask, mineTask);

            var phones   = deadTask.Result;
            var capacity = Math.Max(0, _hostSettings.MaxContainers - mineTask.Result.Count);
            var toTake   = phones.Take(capacity).ToList();
            var takenOver = new ConcurrentBag<Guid>();

            var jobs = toTake.Select(phone => (Func<Task>)(async () =>
            {
                await _supabaseService.AssignPhoneToHostAsync(phone.Id, hostId);
                phone.HostId = hostId;
                var started = await StartPhoneContainerAsync(phone);
                if (!started) return;

                takenOver.Add(phone.Id);
                _logger.LogInformation("[CONTAINER] Taking over phone {PhoneNumber}", phone.Number);
            })).ToList();

            await RunThrottledAsync(jobs);

            await _supabaseService.SetHostStatusAsync(deadHostId, "inactive");

            _logger.LogInformation("[CONTAINER] Takeover complete: {TakenOver}/{Total} phones from host {DeadHostId}", takenOver.Count, phones.Count, deadHostId);
        }
        catch (Exception ex) { _logger.LogError(ex, "[CONTAINER] Error taking over from dead host {DeadHostId}", deadHostId); }
    }

    public async Task<bool> PausePhoneContainerAsync(Phone phone)
    {
        if (_currentHost == null) { _logger.LogError("[CONTAINER] Host not initialized"); return false; }
        try
        {
            _logger.LogInformation("[CONTAINER] Pausing phone {PhoneNumber}", phone.Number);
            if (!string.IsNullOrEmpty(phone.ContainerId))
            {
                await _dockerService.StopContainerAsync(phone.ContainerId);
                await _dockerService.RemoveContainerAsync(phone.ContainerId);
            }

            _portReservations.TryRemove(phone.Id, out _);
            PhonePathHelper.DeleteDirectories(_dockerSettings.DataBasePath, phone.Id);

            await _supabaseService.UpdatePhoneDockerStatusAsync(phone.Id, PhoneDockerStatus.Stopped, containerId: "", containerName: "", dockerUrl: "");
            await _supabaseService.DetachPhoneFromHostAsync(phone.Id);

            _logger.LogInformation("[CONTAINER] Phone {PhoneNumber} paused and detached from host", phone.Number);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CONTAINER] Error pausing phone {PhoneNumber}", phone.Number);
            await _supabaseService.UpdatePhoneDockerStatusAsync(phone.Id, PhoneDockerStatus.Error, errorMessage: ex.Message);
            return false;
        }
    }

    // ── הרצה מקבילית עם הגבלה ─────────────────────────────────────────
    private async Task RunThrottledAsync(IEnumerable<Func<Task>> jobs)
    {
        using var gate = new SemaphoreSlim(_maxParallelStarts);
        await Task.WhenAll(jobs.Select(async job =>
        {
            var qsw = Stopwatch.StartNew();
            await gate.WaitAsync();
            if (qsw.ElapsedMilliseconds > 500)
                _logger.LogInformation("[TIMING] job waited {Ms}ms in queue (MaxParallelStarts={Max})", qsw.ElapsedMilliseconds, _maxParallelStarts);
            try { await job(); }
            catch (Exception ex) { _logger.LogError(ex, "[CONTAINER] Parallel job failed"); }
            finally { gate.Release(); }
        }));
    }
}

record ContainerStatusResponse(string Status);
/// <summary>
/// מה ש-GET /webhooks מחזיר. Webhooks ו-Count קיימים בשני ה-images;
/// Key ו-Redis נוספו ב-cloudapi 1.1.0 ולכן null מול baileys ומול image ישן.
/// </summary>
sealed class WebhookListResponse
{
    // ── שני ה-images מחזירים את השדה הזה בשתי צורות שונות ─────────────
    //   baileys    [ { "url": "…", "registeredAt": "…" } ]   אובייקטים
    //   cloudapi   [ "…" ]                                    מחרוזות
    //
    // List<string> לבד נכשל ב-deserialization מול baileys — וזה לא היה
    // נראה: ה-try/catch סביב הקריאה רשם "Could not clean webhooks" והמשיך,
    // ולכן ניקוי ה-webhooks הישנים לא עבד שם מעולם. הממיר קורא את שתי
    // הצורות ומחזיר url בשני המקרים.
    [System.Text.Json.Serialization.JsonPropertyName("webhooks")]
    [System.Text.Json.Serialization.JsonConverter(typeof(WebhookUrlListConverter))]
    public List<string>? Webhooks { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("count")]
    public int Count { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("key")]
    public string? Key { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("redis")]
    public RedisState? Redis { get; set; }

    internal sealed class RedisState
    {
        [System.Text.Json.Serialization.JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}

/// <summary>
/// קורא `webhooks` בשתי הצורות — מחרוזות (cloudapi) או אובייקטים עם שדה
/// `url` (baileys) — ומחזיר תמיד רשימת כתובות. פריט שאינו אחד מהשניים
/// מדולג ולא מפיל את כל הקריאה: רשומה אחת פגומה לא צריכה להסתיר את השאר.
/// </summary>
sealed class WebhookUrlListConverter : System.Text.Json.Serialization.JsonConverter<List<string>?>
{
    public override List<string>? Read(ref System.Text.Json.Utf8JsonReader reader,
                                       Type typeToConvert,
                                       System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Null) return null;
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray)
            throw new System.Text.Json.JsonException("webhooks must be an array");

        var urls = new List<string>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case System.Text.Json.JsonTokenType.EndArray:
                    return urls;

                case System.Text.Json.JsonTokenType.String:          // cloudapi
                    var s = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) urls.Add(s!);
                    break;

                case System.Text.Json.JsonTokenType.StartObject:     // baileys
                    using (var doc = System.Text.Json.JsonDocument.ParseValue(ref reader))
                    {
                        if (doc.RootElement.TryGetProperty("url", out var u) &&
                            u.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var url = u.GetString();
                            if (!string.IsNullOrWhiteSpace(url)) urls.Add(url!);
                        }
                    }
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }
        return urls;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, List<string>? value,
                               System.Text.Json.JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var v in value) writer.WriteStringValue(v);
        writer.WriteEndArray();
    }
}

/// <summary>
/// מה ש-/health של whatsapp-cloudapi מחזיר. שלושת הדגלים שקובעים אם הטלפון
/// באמת עובד: Graph עונה, הצרכן מחובר, והטוקן לא נשלל.
/// </summary>
public sealed class CloudHealthResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("registered")]
    public bool Registered { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("connected")]
    public bool Connected { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("nats_connected")]
    public bool NatsConnected { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("nats_error")]
    public string? NatsError { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("token_ok")]
    public bool TokenOk { get; set; } = true;
    [System.Text.Json.Serialization.JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; set; }
    /// <summary>true רק כשהכתובת אומתה במטא. null = עוד לא נוסה.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("override_set")]
    public bool? OverrideSet { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("override_error")]
    public string? OverrideError { get; set; }

    /// <summary>
    /// כמה webhooks רשומים ב-redis_shared תחת webhooks:{phone_id}.
    /// 0 = הטלפון אילם למרות ש-/health = 200.
    /// null = image בגרסה שלא מחזירה את השדה — אין מה להסיק, ולא נוגעים.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("webhooks_count")]
    public int? WebhooksCount { get; set; }

    /// <summary>המפתח ב-Redis — אותה מוסכמה כמו ב-whatsapp-single.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("webhooks_key")]
    public string? WebhooksKey { get; set; }

    /// <summary>false = WebhooksCount הוא "לא ידוע", לא "אפס".</summary>
    [System.Text.Json.Serialization.JsonPropertyName("redis_connected")]
    public bool? RedisConnected { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("redis_error")]
    public string? RedisError { get; set; }
}

/// <summary>מדידת שלבים: "prepare_rpc=120ms image=3ms ... total=5400ms"</summary>
internal sealed class StepTimer
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly List<(string Name, long Ms)> _steps = new();
    private long _last;

    public void Mark(string name)
    {
        var now = _sw.ElapsedMilliseconds;
        _steps.Add((name, now - _last));
        _last = now;
    }

    public override string ToString()
    {
        var slowest = _steps.Count > 0 ? _steps.MaxBy(s => s.Ms).Name : "-";
        return string.Join(" ", _steps.Select(s => $"{s.Name}={s.Ms}ms"))
             + $" | total={_sw.ElapsedMilliseconds}ms slowest={slowest}";
    }
}