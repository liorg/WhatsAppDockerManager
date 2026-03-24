using Supabase;
using WhatsAppDockerManager.Configuration;
using WhatsAppDockerManager.Models;
using System.Text.Json;

namespace WhatsAppDockerManager.Services;

public interface ISupabaseService
{
    Task<Host?> GetOrCreateHostAsync(string hostName, string ipAddress, string? externalIp, int portRangeStart, int portRangeEnd, int maxContainers);
    Task UpdateHostHeartbeatAsync(Guid hostId);
    Task<List<Phone>> GetPhonesForHostAsync(Guid hostId);
    Task<List<Phone>> GetOrphanedPhonesAsync();
    Task<Phone?> GetPhoneByIdAsync(Guid phoneId);
    Task UpdatePhoneDockerStatusAsync(Guid phoneId, string status, string? containerId = null, string? containerName = null, int? apiPort = null, int? wsPort = null, string? dockerUrl = null, string? errorMessage = null);
    Task AssignPhoneToHostAsync(Guid phoneId, Guid hostId);
    Task LogContainerEventAsync(Guid? phoneId, Guid? hostId, string eventType, object? eventData = null);
    Task<List<Host>> GetActiveHostsAsync();
    Task<Host?> GetHostByIdAsync(Guid hostId);
    Task SetHostStatusAsync(Guid hostId, string status);
    Task<int> GetNextAvailablePortAsync(Guid hostId, int rangeStart, int rangeEnd);
}

public class SupabaseService : ISupabaseService
{
    private readonly Client _client;
    private readonly ILogger<SupabaseService> _logger;

    public SupabaseService(IConfiguration configuration, ILogger<SupabaseService> logger)
    {
        _logger = logger;
        
        var settings = configuration.GetSection("AppSettings:Supabase").Get<SupabaseSettings>()
            ?? throw new InvalidOperationException("Supabase settings not configured");

        var options = new SupabaseOptions
        {
            AutoConnectRealtime = false
        };

        _client = new Client(settings.Url, settings.Key, options);
    }

    public async Task<Host?> GetOrCreateHostAsync(string hostName, string ipAddress, string? externalIp, int portRangeStart, int portRangeEnd, int maxContainers)
    {
        try
        {
            // Try to find existing host
            var response = await _client.From<Host>()
                .Where(h => h.HostName == hostName)
                .Get();

            var existingHost = response.Models.FirstOrDefault();

            if (existingHost != null)
            {
                // Update existing host
                existingHost.IpAddress = ipAddress;
                existingHost.ExternalIp = externalIp;
                existingHost.LastHeartbeat = DateTime.UtcNow;
                existingHost.Status = HostStatus.Active;
                existingHost.PortRangeStart = portRangeStart;
                existingHost.PortRangeEnd = portRangeEnd;
                existingHost.MaxContainers = maxContainers;

                await _client.From<Host>().Update(existingHost);
                _logger.LogInformation("Updated existing host: {HostName}", hostName);
                return existingHost;
            }

            // Create new host
            var newHost = new Host
            {
                Id = Guid.NewGuid(),
                HostName = hostName,
                IpAddress = ipAddress,
                ExternalIp = externalIp,
                Status = HostStatus.Active,
                LastHeartbeat = DateTime.UtcNow,
                MaxContainers = maxContainers,
                PortRangeStart = portRangeStart,
                PortRangeEnd = portRangeEnd,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var insertResponse = await _client.From<Host>().Insert(newHost);
            _logger.LogInformation("Created new host: {HostName}", hostName);
            return insertResponse.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetOrCreateHostAsync for {HostName}", hostName);
            throw;
        }
    }

    public async Task UpdateHostHeartbeatAsync(Guid hostId)
    {
        try
        {
            var host = await GetHostByIdAsync(hostId);
            if (host != null)
            {
                host.LastHeartbeat = DateTime.UtcNow;
                await _client.From<Host>().Update(host);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating heartbeat for host {HostId}", hostId);
        }
    }

    public async Task<List<Phone>> GetPhonesForHostAsync(Guid hostId)
    {
        try
        {
            var response = await _client.From<Phone>()
                .Where(p => p.HostId == hostId)
                .Where(p => p.Status == "active")
                .Get();

            return response.Models;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting phones for host {HostId}", hostId);
            return new List<Phone>();
        }
    }

    public async Task<List<Phone>> GetOrphanedPhonesAsync()
    {
        try
        {
            // Get phones that have no host assigned or are pending
            var response = await _client.From<Phone>()
                .Where(p => p.Status == "active")
                .Where(p => p.DockerStatus == PhoneDockerStatus.Unknown || p.DockerStatus == PhoneDockerStatus.Pending)
                .Get();

            return response.Models.Where(p => p.HostId == null).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting orphaned phones");
            return new List<Phone>();
        }
    }

    public async Task<Phone?> GetPhoneByIdAsync(Guid phoneId)
    {
        try
        {
            var response = await _client.From<Phone>()
                .Where(p => p.Id == phoneId)
                .Get();

            return response.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting phone {PhoneId}", phoneId);
            return null;
        }
    }

    public async Task UpdatePhoneDockerStatusAsync(Guid phoneId, string status, string? containerId = null, string? containerName = null, int? apiPort = null, int? wsPort = null, string? dockerUrl = null, string? errorMessage = null)
    {
        try
        {
            var phone = await GetPhoneByIdAsync(phoneId);
            if (phone != null)
            {
                phone.DockerStatus = status;
                phone.LastHealthCheck = DateTime.UtcNow;
                
                if (containerId != null) phone.ContainerId = containerId;
                if (containerName != null) phone.ContainerName = containerName;
                if (apiPort != null) phone.ApiPort = apiPort;
                if (wsPort != null) phone.WsPort = wsPort;
                if (dockerUrl != null) phone.DockerUrl = dockerUrl;
                if (errorMessage != null) phone.ErrorMessage = errorMessage;
                
                // Clear error if status is running
                if (status == PhoneDockerStatus.Running)
                {
                    phone.ErrorMessage = null;
                }

                await _client.From<Phone>().Update(phone);
                _logger.LogDebug("Updated phone {PhoneId} docker status to {Status}", phoneId, status);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating phone {PhoneId} docker status", phoneId);
        }
    }

    public async Task AssignPhoneToHostAsync(Guid phoneId, Guid hostId)
    {
        try
        {
            var phone = await GetPhoneByIdAsync(phoneId);
            if (phone != null)
            {
                phone.HostId = hostId;
                phone.DockerStatus = PhoneDockerStatus.Pending;
                await _client.From<Phone>().Update(phone);
                _logger.LogInformation("Assigned phone {PhoneId} to host {HostId}", phoneId, hostId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error assigning phone {PhoneId} to host {HostId}", phoneId, hostId);
        }
    }

    public async Task LogContainerEventAsync(Guid? phoneId, Guid? hostId, string eventType, object? eventData = null)
    {
        try
        {
            var evt = new ContainerEvent
            {
                Id = Guid.NewGuid(),
                PhoneId = phoneId,
                HostId = hostId,
                EventType = eventType,
                EventData = eventData != null ? JsonSerializer.Serialize(eventData) : null,
                CreatedAt = DateTime.UtcNow
            };

            await _client.From<ContainerEvent>().Insert(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging container event {EventType}", eventType);
        }
    }

    public async Task<List<Host>> GetActiveHostsAsync()
    {
        try
        {
            var response = await _client.From<Host>()
                .Where(h => h.Status == HostStatus.Active)
                .Get();

            return response.Models;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active hosts");
            return new List<Host>();
        }
    }

    public async Task<Host?> GetHostByIdAsync(Guid hostId)
    {
        try
        {
            var response = await _client.From<Host>()
                .Where(h => h.Id == hostId)
                .Get();

            return response.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting host {HostId}", hostId);
            return null;
        }
    }

    public async Task SetHostStatusAsync(Guid hostId, string status)
    {
        try
        {
            var host = await GetHostByIdAsync(hostId);
            if (host != null)
            {
                host.Status = status;
                await _client.From<Host>().Update(host);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting host {HostId} status to {Status}", hostId, status);
        }
    }

    public async Task<int> GetNextAvailablePortAsync(Guid hostId, int rangeStart, int rangeEnd)
    {
        try
        {
            var phones = await GetPhonesForHostAsync(hostId);
            var usedPorts = phones
                .Where(p => p.ApiPort.HasValue)
                .Select(p => p.ApiPort!.Value)
                .ToHashSet();

            for (int port = rangeStart; port <= rangeEnd; port++)
            {
                if (!usedPorts.Contains(port))
                {
                    return port;
                }
            }

            throw new InvalidOperationException($"No available ports in range {rangeStart}-{rangeEnd}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting next available port for host {HostId}", hostId);
            throw;
        }
    }
}
