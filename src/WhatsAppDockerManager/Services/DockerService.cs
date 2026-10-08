using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using WhatsAppDockerManager.Configuration;
using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services;

public interface IDockerService
{
    Task<bool> PullImageAsync(string imageName);
    Task<string?> CreateAndStartContainerAsync(Phone phone, int fastApiPort, int baileysPort,
        int authRevision = 0, string maskedUsername = "****user", string? imageName = null,
        string provider = "baileys", CloudApiSpec? cloud = null);
    Task<bool> StopContainerAsync(string containerId);
    Task<bool> RemoveContainerAsync(string containerId);
    Task<ContainerInspectResponse?> InspectContainerAsync(string containerId);
    Task<bool> IsContainerRunningAsync(string containerId);
    Task<IList<ContainerListResponse>> ListContainersAsync(bool all = false);
    Task<bool> CheckHealthAsync(string containerId, int apiPort);
    Task EnsureNetworkExistsAsync(string networkName);
    Task EnsureRedisContainerRunningAsync();
    Task<DockerImageInfo?> GetImageInfoAsync(string imageName);
    Task RemoveContainerByNameAsync(string containerName);
}

// ── Model ─────────────────────────────────────────────────────────────
public class DockerImageInfo
{
    public string?       Id       { get; set; }
    public DateTime      Created  { get; set; }
    public List<string>? RepoTags { get; set; }
}

/// <summary>
/// המזהים שקונטיינר cloudapi צריך. מגיעים מ-phones (או מ-start_phone_prepare
/// אם תקפל אותם לשם) — לא מ-appsettings, כי הם פר-טלפון.
/// </summary>
public sealed class CloudApiSpec
{
    public string  WabaId        { get; set; } = "";
    public string  PhoneNumberId { get; set; } = "";
    public string  AccessToken   { get; set; } = "";
    public string? VerifyToken   { get; set; }
}

public class DockerService : IDockerService, IDisposable
{
    private readonly DockerClient _client;
    private readonly ILogger<DockerService> _logger;
    private readonly DockerSettings _dockerSettings;
    private readonly HostSettings _hostSettings;
    private readonly IConfiguration _configuration;

    private const string RedisContainerName = "redis_shared";
    private const string NetworkName        = "whatsapp_network";
    private readonly uint _stopTimeoutSeconds;

    public DockerService(IConfiguration configuration, ILogger<DockerService> logger)
    {
        _logger         = logger;
        _configuration  = configuration;
        _dockerSettings = configuration.GetSection("AppSettings:Docker").Get<DockerSettings>() ?? new();
        _hostSettings   = configuration.GetSection("AppSettings:Host").Get<HostSettings>() ?? new();
        _stopTimeoutSeconds = (uint)Math.Max(0, configuration.GetValue<int?>("AppSettings:Docker:StopTimeoutSeconds") ?? 10);

        var dockerUri = GetDockerUri();
        _logger.LogInformation("Connecting to Docker at {Uri}", dockerUri);
        _client = new DockerClientConfiguration(new Uri(dockerUri)).CreateClient();
    }

    private static string GetDockerUri()
    {
        if (OperatingSystem.IsWindows()) return "npipe://./pipe/docker_engine";
        return "unix:///var/run/docker.sock";
    }

    private string Cfg(string key, string fallback = "")
        => _configuration[key] is { Length: > 0 } v ? v : fallback;

    /// <summary>
    /// טוקן ה-whqueue של טלפון בודד: HMAC(master, "v{version}:{phone_id}").
    /// ה-master לא נכנס לשום קונטיינר — כל קונטיינר מקבל רק את הטוקן שלו,
    /// ולכן קונטיינר פרוץ יכול לרשום מחדש רק את עצמו. whqueue גוזר את אותו
    /// ערך בכל בקשה, ולכן הוא לא נשמר בשום מקום.
    /// </summary>
    public string WhqueuePhoneToken(Guid phoneId)
    {
        var master = Cfg("AppSettings:CloudApi:WhqueueMasterSecret");
        if (string.IsNullOrEmpty(master))
            throw new InvalidOperationException(
                "AppSettings:CloudApi:WhqueueMasterSecret is not configured — cloudapi containers cannot register");

        var version = Cfg("AppSettings:CloudApi:WhqueueTokenVersion", "1");
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(master));
        var hash = h.ComputeHash(Encoding.UTF8.GetBytes($"v{version}:{phoneId}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<bool> PullImageAsync(string imageName)
    {
        try
        {
            _logger.LogInformation("[DOCKER] Pulling Docker image: {ImageName}", imageName);
            var progress = new Progress<JSONMessage>(message =>
            {
                if (!string.IsNullOrEmpty(message.Status))
                    _logger.LogDebug("[DOCKER] Pull progress: {Status} {Progress}", message.Status, message.ProgressMessage);
            });
            var parts = imageName.Split(':');
            var name  = parts[0];
            var tag   = parts.Length > 1 ? parts[1] : "latest";
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = name, Tag = tag }, null, progress);
            _logger.LogInformation("[DOCKER] Successfully pulled image: {ImageName}", imageName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DOCKER] Failed to pull image: {ImageName}", imageName);
            return false;
        }
    }

    // ── GetImageInfoAsync ─────────────────────────────────────────────
    public async Task<DockerImageInfo?> GetImageInfoAsync(string imageName)
    {
        try
        {
            var images = await _client.Images.ListImagesAsync(
                new ImagesListParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["reference"] = new Dictionary<string, bool> { [imageName] = true }
                    }
                });

            var img = images.FirstOrDefault();
            if (img == null) return null;

            return new DockerImageInfo
            {
                Id       = img.ID,
                Created  = img.Created,
                RepoTags = img.RepoTags?.ToList(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get image info for {Image}", imageName);
            return null;
        }
    }

    public async Task RemoveContainerByNameAsync(string containerName)
    {
        try
        {
            var containers = await _client.Containers
                .ListContainersAsync(new ContainersListParameters { All = true });
            var existing = containers.FirstOrDefault(c =>
                c.Names.Any(n => n.TrimStart('/') == containerName));
            if (existing == null)
            {
                _logger.LogInformation("[DOCKER] No container found with name {Name} — nothing to remove", containerName);
                return;
            }
            await RemoveContainerAsync(existing.ID);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DOCKER] RemoveContainerByNameAsync failed for {Name}", containerName);
        }
    }

    public async Task<string?> CreateAndStartContainerAsync(Phone phone, int fastApiPort, int baileysPort,
        int authRevision = 0, string maskedUsername = "****user", string? imageName = null,
        string provider = "baileys", CloudApiSpec? cloud = null)
    {
        var sw    = Stopwatch.StartNew();
        long last = 0;
        var steps = new List<string>();
        void Mark(string n) { var now = sw.ElapsedMilliseconds; steps.Add($"{n}={now - last}ms"); last = now; }

        var isCloud = string.Equals(provider, "cloudapi", StringComparison.OrdinalIgnoreCase);

        try
        {
            var image = string.IsNullOrWhiteSpace(imageName) ? _dockerSettings.ImageName : imageName;   // image לפי provider

            _logger.LogInformation("[DOCKER] Creating container | phone={Phone} provider={Provider} image={Image} fastApi={FastApi} baileys={Baileys}",
                phone.Number, provider, image, fastApiPort, isCloud ? 0 : baileysPort);

            var containerName = PhonePathHelper.ContainerName(phone.Number, phone.Id);
            var basePath      = _dockerSettings.DataBasePath;
            var authPath      = PhonePathHelper.AuthPath(basePath, phone.Id);
            var logsPath      = PhonePathHelper.LogsPath(basePath, phone.Id);
            var contactsPath  = PhonePathHelper.ContactsPath(basePath, phone.Id);

            PhonePathHelper.EnsureDirectoriesExist(basePath, phone.Id);
            Mark("dirs");

            // הסר container קיים עם אותו שם
            var existingContainers = await _client.Containers
                .ListContainersAsync(new ContainersListParameters { All = true });
            var existing = existingContainers.FirstOrDefault(c =>
                c.Names.Any(n => n.TrimStart('/') == containerName));
            Mark("list");
            if (existing != null)
            {
                _logger.LogWarning("[DOCKER] Container {Name} already exists, removing...", containerName);
                await RemoveContainerAsync(existing.ID);
                Mark("remove_existing");
            }

            // TrimEnd — הכתובת הזאת נזרעת ל-Redis, ואותה כתובת נרשמת מ-
            // ContainerManager. לוכסן עוקב כאן היה מייצר "…:5000//api/…",
            // כלומר רשומה **שנייה** ב-SET שהדה-דופליקציה לא תזהה.
            var managerUrl = Cfg("AppSettings:ManagerUrl", "http://172.17.0.1:5000").TrimEnd('/');

            var createParams = isCloud
                ? BuildCloudApiParams(phone, containerName, image, fastApiPort, authRevision,
                                      maskedUsername, managerUrl, logsPath, cloud)
                : BuildBaileysParams(phone, containerName, image, fastApiPort, baileysPort, authRevision,
                                     maskedUsername, managerUrl, authPath, logsPath, contactsPath);

            var createResponse = await _client.Containers.CreateContainerAsync(createParams);
            Mark("create");
            _logger.LogInformation("[DOCKER] Container {Name} created with ID {Id}", containerName, createResponse.ID);

            // ── cloudapi: לחבר גם לרשת של NATS ───────────────────────────
            // הקונטיינר נוצר על whatsapp_network (bridge), ו-whqueue רץ
            // ב-Swarm על overlay. bridge לא פותר שמות ב-overlay, ולכן
            // nats://nats-1:4222 לא היה מתרגם. חיבור לרשת השנייה פותר את זה
            // בלי לחשוף שום פורט החוצה — הרשת חייבת להיות attachable.
            if (isCloud)
            {
                await AttachToNatsNetworkAsync(createResponse.ID, containerName);
                Mark("nats_network");
            }

            var started = await _client.Containers
                .StartContainerAsync(createResponse.ID, new ContainerStartParameters());
            Mark("start");

            if (!started)
            {
                _logger.LogError("[DOCKER] Failed to start container {Name}", containerName);
                return null;
            }

            _logger.LogInformation("[DOCKER] ✓ Container {Name} started. FastAPI:{FastApi} Baileys:{Baileys}",
                containerName, fastApiPort, isCloud ? 0 : baileysPort);
            _logger.LogInformation("[TIMING][DOCKER] {Name} {Steps} | total={Ms}ms",
                containerName, string.Join(" ", steps), sw.ElapsedMilliseconds);
            return createResponse.ID;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DOCKER] Error creating container for phone {Phone}", phone.Number);
            return null;
        }
    }

    // ── baileys — ללא שינוי מההתנהגות הקיימת ──────────────────────────
    private CreateContainerParameters BuildBaileysParams(
        Phone phone, string containerName, string image, int fastApiPort, int baileysPort,
        int authRevision, string maskedUsername, string managerUrl,
        string authPath, string logsPath, string contactsPath)
        => new()
        {
            Image = image,
            Name  = containerName,
            Env   = new List<string>
            {
                $"TZ={_dockerSettings.Timezone}",
                $"PHONE_NUMBER={phone.Number}",
                $"PHONE_ID={phone.Id}",
                $"REDIS_URL=redis://{RedisContainerName}:6379",
                $"AUTH_REVISION={authRevision}",
                $"USER_DISPLAY={maskedUsername}",
                $"MANAGER_URL={managerUrl}",
                $"USE_PAIRING_CODE={(phone.UsePairingCode ? "true" : "false")}",
            },
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                { "8000/tcp", default },
                { "3001/tcp", default }
            },
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    { "8000/tcp", new List<PortBinding> { new() { HostPort = fastApiPort.ToString() } } },
                    { "3001/tcp", new List<PortBinding> { new() { HostPort = baileysPort.ToString() } } }
                },
                Binds = new List<string>
                {
                    $"{authPath}:/app/auth_info",
                    $"{logsPath}:/var/log",
                    $"{contactsPath}:/app/data"
                },
                NetworkMode   = NetworkName,
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                Memory    = 512 * 1024 * 1024,
                CPUShares = 512
            },
            Labels = new Dictionary<string, string>
            {
                { "app",          "whatsapp-manager" },
                { "provider",     "baileys" },
                { "phone_id",     phone.Id.ToString() },
                { "phone_number", phone.Number },
                { "image",        image },
                { "fastapi_port", fastApiPort.ToString() },
                { "baileys_port", baileysPort.ToString() }
            }
        };

    // ── cloudapi ───────────────────────────────────────────────────────
    // אין session מקומי, אין socket, ואין פורט Baileys. מה שכן יש: המזהים
    // של מטא, וטוקן whqueue של הטלפון הזה בלבד.
    private CreateContainerParameters BuildCloudApiParams(
        Phone phone, string containerName, string image, int fastApiPort,
        int authRevision, string maskedUsername, string managerUrl,
        string logsPath, CloudApiSpec? cloud)
    {
        if (cloud == null || string.IsNullOrWhiteSpace(cloud.PhoneNumberId))
            throw new InvalidOperationException(
                $"phone {phone.Number} is cloudapi but has no phone_number_id — refusing to start a mute container");

        var accessToken = !string.IsNullOrWhiteSpace(cloud.AccessToken)
            ? cloud.AccessToken
            : Cfg("AppSettings:Meta:AccessToken");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException($"phone {phone.Number} has no Cloud API access token");

        // הכתובת שהקונטיינר מפרסם ל-whqueue כדי שמדיה תגיע חזרה דרך ה-NAT
        var host = !string.IsNullOrEmpty(_hostSettings.ExternalIp) ? _hostSettings.ExternalIp
                 : !string.IsNullOrEmpty(_hostSettings.IpAddress)  ? _hostSettings.IpAddress
                 : "localhost";

        var env = new List<string>
        {
            $"TZ={_dockerSettings.Timezone}",
            $"PHONE_NUMBER={phone.Number}",
            $"PHONE_ID={phone.Id}",
            $"AUTH_REVISION={authRevision}",
            $"USER_DISPLAY={maskedUsername}",

            $"WABA_ID={cloud.WabaId}",
            $"PHONE_NUMBER_ID={cloud.PhoneNumberId}",
            $"WA_ACCESS_TOKEN={accessToken}",
            $"GRAPH_VERSION={Cfg("AppSettings:Meta:GraphVersion", "v21.0")}",

            $"LISTEN_PORT=8000",
            $"PUBLIC_BASE_URL=http://{host}:{fastApiPort}",

            // ── ה-webhooks ─────────────────────────────────────────────────
            // **אותו** Redis שמקבל קונטיינר baileys, ואותה מוסכמת מפתח:
            // webhooks:{PHONE_ID} כ-SET, רשומה {url,secret,registeredAt}.
            // שני הספקים חולקים חנות אחת, ולכן גם את ההתנהגות אחרי restart.
            $"REDIS_URL=redis://{RedisContainerName}:6379",
            // הזרע. הקונטיינר כותב אותו ל-Redis בעלייה אם הוא לא שם, ולכן
            // הרכבה מחדש לא תלויה ברישום מה-Manager שיגיע רק אחרי readiness.
            $"MANAGER_WEBHOOK_URL={managerUrl}/api/webhook/container-event/{phone.Id}",
            $"WEBHOOK_SECRET={Cfg("AppSettings:WebhookSecret", "manager-secret")}",

            $"WHQUEUE_BASE_URL={Cfg("AppSettings:CloudApi:WhqueueBaseUrl", "https://whqueue.grossman.bot")}",
            // ה-bootstrap היחיד. כוחו: "תן לי את ה-credentials של עצמי".
            // ה-JWT ל-NATS נמשך איתו בזמן ריצה ונשאר בזיכרון בלבד.
            $"WHQUEUE_PHONE_TOKEN={WhqueuePhoneToken(phone.Id)}",
            $"NATS_ACK_WAIT={Cfg("AppSettings:CloudApi:NatsAckWait", "60")}",
            $"NATS_MAX_DELIVER={Cfg("AppSettings:CloudApi:NatsMaxDeliver", "5")}",
            // ה-verify token הוא של whqueue ומשותף לכל המספרים, כי יש כתובת
            // callback אחת. טוקן פר טלפון היה נדחה במטא וה-override לא היה נכנס.
            // phones.cloud_verify_token גובר, אם מולא, כדי לא לשבור מספר קיים.
            $"WEBHOOK_VERIFY_TOKEN={(string.IsNullOrWhiteSpace(cloud.VerifyToken) ? Cfg("AppSettings:CloudApi:WhqueueVerifyToken") : cloud.VerifyToken)}",
        };

        // APP_SECRET ו-WhqueueMasterSecret לא נכנסים לכאן בכוונה: את החתימות
        // של מטא מאמת רק whqueue, וה-master נשאר ב-Manager.
        var agentToken = Cfg("AppSettings:AgentToken");
        if (!string.IsNullOrEmpty(agentToken))
            env.Add($"AGENT_TOKEN={agentToken}");

        return new CreateContainerParameters
        {
            Image = image,
            Name  = containerName,
            Env   = env,
            ExposedPorts = new Dictionary<string, EmptyStruct> { { "8000/tcp", default } },
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    { "8000/tcp", new List<PortBinding> { new() { HostPort = fastApiPort.ToString() } } }
                },
                // רק logs. אין auth_info (אין session), ואין data: ה-webhooks
                // יושבים ב-redis_shared, בדיוק כמו ב-baileys. קובץ מקומי **וגם**
                // Redis היו שני מקורות אמת — וזו בדיוק הכפילות שרצינו למנוע.
                Binds = new List<string> { $"{logsPath}:/var/log" },
                NetworkMode   = NetworkName,
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                Memory    = 256 * 1024 * 1024,   // בלי Baileys — חצי מהזיכרון מספיק
                CPUShares = 256
            },
            Labels = new Dictionary<string, string>
            {
                { "app",             "whatsapp-manager" },
                { "provider",        "cloudapi" },
                { "phone_id",        phone.Id.ToString() },
                { "phone_number",    phone.Number },
                { "image",           image },
                { "fastapi_port",    fastApiPort.ToString() },
                { "phone_number_id", cloud.PhoneNumberId }
            }
        };
    }

    public async Task<bool> StopContainerAsync(string containerId)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            await _client.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = _stopTimeoutSeconds });
            _logger.LogInformation("[TIMING][DOCKER] Container {ContainerId} stopped in {Ms}ms (timeout={Sec}s)",
                containerId[..Math.Min(12, containerId.Length)], sw.ElapsedMilliseconds, _stopTimeoutSeconds);
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Error stopping container {ContainerId}", containerId); return false; }
    }

    public async Task<bool> RemoveContainerAsync(string containerId)
    {
        try
        {
            // Force=true — מסיר גם container רץ. בלי Stop נוסף (חסך עד StopTimeoutSeconds)
            var sw = Stopwatch.StartNew();
            await _client.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = false });
            _logger.LogInformation("[TIMING][DOCKER] Container {ContainerId} removed in {Ms}ms",
                containerId[..Math.Min(12, containerId.Length)], sw.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Error removing container {ContainerId}", containerId); return false; }
    }

    public async Task<ContainerInspectResponse?> InspectContainerAsync(string containerId)
    {
        try { return await _client.Containers.InspectContainerAsync(containerId); }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Error inspecting container {ContainerId}", containerId); return null; }
    }

    public async Task<bool> IsContainerRunningAsync(string containerId)
    {
        try { var i = await InspectContainerAsync(containerId); return i?.State?.Running ?? false; }
        catch { return false; }
    }

    public async Task<IList<ContainerListResponse>> ListContainersAsync(bool all = false)
    {
        try
        {
            _logger.LogInformation("[DOCKER] Listing containers (all={All})", all);
            return await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All     = all,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    { "label", new Dictionary<string, bool> { { "app=whatsapp-manager", true } } }
                }
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Error listing containers"); return new List<ContainerListResponse>(); }
    }

    public async Task<bool> CheckHealthAsync(string containerId, int apiPort)
    {
        try
        {
            _logger.LogInformation("[DOCKER]  Checking health of container {ContainerId} on port {Port}", containerId, apiPort);
            if (!await IsContainerRunningAsync(containerId)) return false;
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync($"http://localhost:{apiPort}/health");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// מחבר קונטיינר cloudapi לרשת שבה יושב NATS.
    ///
    /// הרשת נוצרת על ידי ה-stack של whqueue ולכן **לא** נוצרת כאן: יצירה
    /// אוטומטית הייתה מייצרת bridge מקומי בשם זהה, שנראה תקין ולא מחובר
    /// לשום דבר — כשל שקט. בלי הרשת הקונטיינר לא יקבל הודעות נכנסות, ולכן
    /// זה נכשל ברעש.
    /// </summary>
    private async Task AttachToNatsNetworkAsync(string containerId, string containerName)
    {
        var network = Cfg("AppSettings:CloudApi:NatsNetwork", "whqueue-net");
        if (string.IsNullOrWhiteSpace(network))
            return;   // ריק = הקונטיינרים מגיעים ל-NATS בדרך אחרת (פורט מפורסם)

        try
        {
            await _client.Networks.ConnectNetworkAsync(network,
                new NetworkConnectParameters { Container = containerId });
            _logger.LogInformation("[DOCKER] {Name} attached to {Network}", containerName, network);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not attach {containerName} to '{network}'. The network must exist and be " +
                $"attachable (docker network create --driver overlay --attachable {network}, or the " +
                $"whqueue stack). Without it the phone cannot reach NATS and will receive no messages.",
                ex);
        }
    }

    public async Task EnsureNetworkExistsAsync(string networkName)
    {
        try
        {
            var networks = await _client.Networks.ListNetworksAsync(new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    { "name", new Dictionary<string, bool> { { networkName, true } } }
                }
            });
            if (networks.Any(n => n.Name == networkName))
            {
                _logger.LogInformation("Network {Network} already exists", networkName);
                return;
            }
            await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters { Name = networkName, Driver = "bridge" });
            _logger.LogInformation("Created Docker network: {Network}", networkName);
        }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Failed to ensure network {Network} exists", networkName); throw; }
    }

    public async Task EnsureRedisContainerRunningAsync()
    {
        const string containerName = "redis_shared";
        const string imageName     = "redis:7-alpine";
        const string networkName   = "whatsapp_network";
        try
        {
            var containers = await _client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true });
            var existing = containers.FirstOrDefault(c =>
                c.Names.Any(n => n.TrimStart('/') == containerName));

            if (existing != null)
            {
                // ההגדרות למטה נקבעות ב-**יצירה** בלבד. קונטיינר שנוצר לפני
                // השינוי הזה ממשיך בלי maxmemory ובלי volume, ולכן נשאר חשוף
                // ל-OOM-kill — וזה לא ייראה כתקלה ב-Redis אלא כטלפונים שנאלמו.
                // לא מרכיבים אותו מחדש מכאן (זה היה מוחק את הרישום של כולם),
                // אלא מתעדים בדיוק מה להריץ.
                _logger.LogWarning(
                    "[DOCKER] redis_shared exists — created before the memory guard. " +
                    "Apply live:  docker exec redis_shared redis-cli config set maxmemory 384mb && " +
                    "docker exec redis_shared redis-cli config set maxmemory-policy volatile-lru   " +
                    "(a volume for /data needs a one-time recreate — see CHANGELOG)");

                if (existing.State == "running") { _logger.LogInformation("[DOCKER] Redis container already running"); return; }
                await _client.Containers.StartContainerAsync(existing.ID, new ContainerStartParameters());
                _logger.LogInformation("[DOCKER] Started existing Redis container");
                return;
            }

            await PullImageAsync(imageName);

            // ── למה maxmemory, ולמה volatile-lru דווקא ────────────────────
            // ה-Redis הזה מחזיק עכשיו את webhooks:{phone_id} של **כל** הטלפונים,
            // ולכן הוא load-bearing: נפילה שלו היא לא איבוד היסטוריה אלא
            // השתקה של כל הטלפונים בבת אחת.
            //
            // בלי maxmemory, Redis מבקש זיכרון עד שה-cgroup של Docker הורג
            // אותו ב-OOM — בלי להשליך כלום לפני, ובלי הזדמנות להתגונן. עם
            // maxmemory נמוך מהתקרה של הקונטיינר הוא משליך בעצמו, בשליטה.
            //
            // וה-policy הוא העיקר: allkeys-lru היה משליך מפתח webhooks כדי
            // לפנות מקום להיסטוריית הודעות — טלפון שנאלם בשקט כדי לשמור משהו
            // שאף אחד לא קורא. volatile-lru נוגע **רק** במפתחות עם TTL, ורק
            // ל-stream של ההודעות יש TTL. אז מה שנזרק תחת לחץ הוא בדיוק מה
            // שמותר לזרוק.
            const long memLimit  = 512L * 1024 * 1024;   // תקרת הקונטיינר
            const string maxMem  = "384mb";               // Redis משליך לפני כן

            var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image      = imageName,
                Name       = containerName,
                Cmd        = new List<string>
                {
                    "redis-server",
                    "--maxmemory",        maxMem,
                    "--maxmemory-policy", "volatile-lru",
                    // ה-webhooks צריכים לשרוד restart של Redis עצמו, לא רק של
                    // הקונטיינרים. appendonly נותן את זה; save לבד מאבד את מה
                    // שנכתב מאז ה-snapshot האחרון.
                    "--appendonly",       "yes",
                    "--appendfsync",      "everysec",
                },
                // הקונטיינרים מגיעים ל-Redis דרך הרשת כ-redis_shared:6379, אבל
                // תהליכים על ה-host (ה-Manager עצמו, ה-Worker) מוגדרים מול
                // localhost:6379 — ובלי פרסום הפורט הם לא מגיעים לשם בכלל.
                // 127.0.0.1 ולא 0.0.0.0: Redis בלי סיסמה שמאזין לעולם הוא
                // השתלטות על השרת, לא תקלת חיבור.
                ExposedPorts = new Dictionary<string, EmptyStruct> { { "6379/tcp", default } },
                HostConfig = new HostConfig
                {
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                    Memory = memLimit,
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        { "6379/tcp", new List<PortBinding> { new() { HostIP = "127.0.0.1", HostPort = "6379" } } }
                    },
                    // בלי זה כל `docker rm redis_shared` מוחק את הרישום של כל
                    // הטלפונים — baileys כולל.
                    Binds = new List<string> { "redis_shared_data:/data" },
                }
            });
            await _client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
            await _client.Networks.ConnectNetworkAsync(networkName,
                new NetworkConnectParameters { Container = response.ID });
            _logger.LogInformation("[DOCKER] Redis container created and started");
        }
        catch (Exception ex) { _logger.LogError(ex, "[DOCKER] Failed to ensure Redis container"); throw; }
    }

    public void Dispose() { _client?.Dispose(); }
}