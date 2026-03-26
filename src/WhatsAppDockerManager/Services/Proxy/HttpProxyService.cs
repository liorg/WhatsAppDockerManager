using System.Collections.Concurrent;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;
using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services.Proxy;

/// <summary>
/// Dynamic route provider for YARP that routes requests to the correct container.
/// 
/// Routes available for Frontend:
/// - /wa/{phoneNumber}/status          → Container /status
/// - /wa/{phoneNumber}/qrcode          → Container /qrcode
/// - /wa/{phoneNumber}/qrcode/image    → Container /qrcode/image
/// - /wa/{phoneNumber}/send/text       → Container /send/text
/// - /wa/{phoneNumber}/send/buttons    → Container /send/buttons
/// - /wa/{phoneNumber}/send/list       → Container /send/list
/// - /wa/{phoneNumber}/contacts        → Container /contacts
/// - /wa/{phoneNumber}/auth/dashboard  → Container /auth/dashboard
/// - /wa/{phoneNumber}/health          → Container /health
/// - /wa/{phoneNumber}/**              → Container /** (catch-all)
/// 
/// Also supports routing by phone ID:
/// - /wa/id/{phoneId}/**               → Container /**
/// </summary>
public class DynamicProxyConfigProvider : IProxyConfigProvider, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DynamicProxyConfigProvider> _logger;
    private readonly ConcurrentDictionary<string, PhoneRouteInfo> _phoneRoutes = new();
    private readonly ConcurrentDictionary<Guid, PhoneRouteInfo> _phoneRoutesById = new();
    private CancellationTokenSource _changeToken = new();
    private volatile IProxyConfig _config;

    public DynamicProxyConfigProvider(
        IServiceProvider serviceProvider,
        ILogger<DynamicProxyConfigProvider> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = new InMemoryConfig(new List<RouteConfig>(), new List<ClusterConfig>());
    }

    public IProxyConfig GetConfig() => _config;

    public void UpdateRoutes(IEnumerable<Phone> phones)
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        foreach (var phone in phones.Where(p => p.DockerStatus == PhoneDockerStatus.Running && p.ApiPort.HasValue))
        {
            var phoneKey = phone.Number.Replace("+", "");
            var clusterId = $"cluster-{phoneKey}";

            // Create cluster (destination) - points to the container's FastAPI
            clusters.Add(new ClusterConfig
            {
                ClusterId = clusterId,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    {
                        "default",
                        new DestinationConfig
                        {
                            Address = $"http://localhost:{phone.ApiPort}"
                        }
                    }
                },
                HttpClient = new HttpClientConfig
                {
                    RequestHeaderEncoding = "utf-8"
                }
            });

            // Main route by phone number: /wa/{phoneNumber}/**
            routes.Add(new RouteConfig
            {
                RouteId = $"route-{phoneKey}",
                ClusterId = clusterId,
                Match = new RouteMatch
                {
                    Path = $"/wa/{phoneKey}/{{**catch-all}}"
                },
                Transforms = new List<IReadOnlyDictionary<string, string>>
                {
                    new Dictionary<string, string>
                    {
                        { "PathRemovePrefix", $"/wa/{phoneKey}" }
                    }
                }
            });

            // Route by phone ID: /wa/id/{phoneId}/**
            routes.Add(new RouteConfig
            {
                RouteId = $"route-id-{phone.Id}",
                ClusterId = clusterId,
                Match = new RouteMatch
                {
                    Path = $"/wa/id/{phone.Id}/{{**catch-all}}"
                },
                Transforms = new List<IReadOnlyDictionary<string, string>>
                {
                    new Dictionary<string, string>
                    {
                        { "PathRemovePrefix", $"/wa/id/{phone.Id}" }
                    }
                }
            });

            var routeInfo = new PhoneRouteInfo
            {
                PhoneId = phone.Id,
                PhoneNumber = phone.Number,
                ApiPort = phone.ApiPort!.Value,
                WsPort = phone.WsPort ?? 0
            };

            _phoneRoutes[phoneKey] = routeInfo;
            _phoneRoutesById[phone.Id] = routeInfo;
        }

        // Create new config and signal change
        var oldToken = _changeToken;
        _changeToken = new CancellationTokenSource();
        _config = new InMemoryConfig(routes, clusters, new CancellationChangeToken(_changeToken.Token));
        oldToken.Cancel();

        _logger.LogInformation("Updated proxy routes: {Count} phones", phones.Count());
    }

    public PhoneRouteInfo? GetRouteInfo(string phoneKey)
    {
        return _phoneRoutes.TryGetValue(phoneKey, out var info) ? info : null;
    }

    public PhoneRouteInfo? GetRouteInfoById(Guid phoneId)
    {
        return _phoneRoutesById.TryGetValue(phoneId, out var info) ? info : null;
    }

    public IEnumerable<PhoneRouteInfo> GetAllRoutes() => _phoneRoutes.Values;

    public void Dispose()
    {
        _changeToken.Cancel();
        _changeToken.Dispose();
    }
}

public class PhoneRouteInfo
{
    public Guid PhoneId { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public int ApiPort { get; set; }
    public int WsPort { get; set; }
}

public class InMemoryConfig : IProxyConfig
{
    public IReadOnlyList<RouteConfig> Routes { get; }
    public IReadOnlyList<ClusterConfig> Clusters { get; }
    public IChangeToken ChangeToken { get; }

    public InMemoryConfig(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters,
        IChangeToken? changeToken = null)
    {
        Routes = routes;
        Clusters = clusters;
        ChangeToken = changeToken ?? new CancellationChangeToken(CancellationToken.None);
    }
}

/// <summary>
/// Background service that keeps proxy routes in sync with database
/// </summary>
public class ProxyRouteSyncService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly DynamicProxyConfigProvider _configProvider;
    private readonly ILogger<ProxyRouteSyncService> _logger;

    public ProxyRouteSyncService(
        IServiceProvider serviceProvider,
        DynamicProxyConfigProvider configProvider,
        ILogger<ProxyRouteSyncService> logger)
    {
        _serviceProvider = serviceProvider;
        _configProvider = configProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial sync after startup
        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var containerManager = scope.ServiceProvider.GetRequiredService<IContainerManager>();
                var supabaseService = scope.ServiceProvider.GetRequiredService<ISupabaseService>();

                if (containerManager.CurrentHostId.HasValue)
                {
                    var phones = await supabaseService.GetPhonesForHostAsync(containerManager.CurrentHostId.Value);
                    _configProvider.UpdateRoutes(phones);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing proxy routes");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
