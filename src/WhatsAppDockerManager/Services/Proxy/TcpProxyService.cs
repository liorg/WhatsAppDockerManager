using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WhatsAppDockerManager.Configuration;

namespace WhatsAppDockerManager.Services.Proxy;

/// <summary>
/// TCP Proxy that forwards raw TCP connections to the correct container
/// </summary>
public class TcpProxyService : IHostedService, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TcpProxyService> _logger;
    private readonly ProxySettings _proxySettings;
    private readonly ConcurrentDictionary<int, TcpListener> _listeners = new();
    private readonly ConcurrentDictionary<int, int> _portMappings = new(); // External port -> Container port
    private readonly CancellationTokenSource _cts = new();

    public TcpProxyService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<TcpProxyService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _proxySettings = configuration.GetSection("AppSettings:Proxy").Get<ProxySettings>() ?? new();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TCP Proxy Service starting...");
        
        // The actual listeners will be started when routes are configured
        _ = SyncPortMappingsAsync(_cts.Token);
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TCP Proxy Service stopping...");
        _cts.Cancel();

        foreach (var listener in _listeners.Values)
        {
            listener.Stop();
        }
        _listeners.Clear();

        return Task.CompletedTask;
    }

    private async Task SyncPortMappingsAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(10000, stoppingToken); // Wait for initialization

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
                    
                    foreach (var phone in phones.Where(p => p.WsPort.HasValue && p.DockerStatus == "running"))
                    {
                        // Map external WebSocket port to container
                        var externalPort = _proxySettings.TcpPortStart + (phone.WsPort!.Value - 3001);
                        
                        if (!_listeners.ContainsKey(externalPort))
                        {
                            StartListener(externalPort, phone.WsPort.Value);
                        }
                        _portMappings[externalPort] = phone.WsPort.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing TCP port mappings");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private void StartListener(int externalPort, int internalPort)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, externalPort);
            listener.Start();
            _listeners[externalPort] = listener;

            _logger.LogInformation("TCP Proxy listening on port {ExternalPort} -> {InternalPort}", 
                externalPort, internalPort);

            // Start accepting connections
            _ = AcceptConnectionsAsync(listener, externalPort, _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start TCP listener on port {Port}", externalPort);
        }
    }

    private async Task AcceptConnectionsAsync(TcpListener listener, int externalPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                
                if (_portMappings.TryGetValue(externalPort, out var internalPort))
                {
                    _ = HandleConnectionAsync(client, internalPort, ct);
                }
                else
                {
                    client.Close();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting TCP connection on port {Port}", externalPort);
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, int internalPort, CancellationToken ct)
    {
        TcpClient? upstream = null;
        try
        {
            upstream = new TcpClient();
            await upstream.ConnectAsync("localhost", internalPort, ct);

            var clientStream = client.GetStream();
            var upstreamStream = upstream.GetStream();

            // Bidirectional copy
            var clientToUpstream = CopyStreamAsync(clientStream, upstreamStream, ct);
            var upstreamToClient = CopyStreamAsync(upstreamStream, clientStream, ct);

            await Task.WhenAny(clientToUpstream, upstreamToClient);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TCP proxy connection error");
        }
        finally
        {
            client.Close();
            upstream?.Close();
        }
    }

    private static async Task CopyStreamAsync(NetworkStream source, NetworkStream destination, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var bytesRead = await source.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        
        foreach (var listener in _listeners.Values)
        {
            listener.Stop();
        }
    }
}
