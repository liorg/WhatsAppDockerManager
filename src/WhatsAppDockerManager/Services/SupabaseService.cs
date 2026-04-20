using WhatsAppDockerManager.Configuration;
using WhatsAppDockerManager.Models;
using System.Text.Json;
using DbHost = WhatsAppDockerManager.Models.Host;
namespace WhatsAppDockerManager.Services;
using Supabase;

public interface ISupabaseService
{
    // Host operations
    Task<DbHost?> GetOrCreateHostAsync(string hostName, string ipAddress, string? externalIp, int portRangeStart, int portRangeEnd, int maxContainers);
    Task UpdateHostHeartbeatAsync(Guid hostId);
    Task<List<DbHost>> GetActiveHostsAsync();
    Task<DbHost?> GetHostByIdAsync(Guid hostId);
    Task SetHostStatusAsync(Guid hostId, string status);
    Task<int> GetNextAvailablePortAsync(Guid hostId, int rangeStart, int rangeEnd);
    Task<List<DbHost>> GetDeadHostsAsync(int heartbeatTimeoutMinutes = 5);

    // Phone operations
    Task<List<Phone>> GetPhonesForHostAsync(Guid hostId);
    Task<List<Phone>> GetOrphanedPhonesAsync();
    Task<Phone?> GetPhoneByIdAsync(Guid phoneId);
    Task<Phone?> GetPhoneByNumberAsync(string phoneNumber);
    Task<Phone> CreatePhoneAsync(Phone phone);
    Task<List<Phone>> GetAllPhonesAsync();
    Task UpdatePhoneDockerStatusAsync(Guid phoneId, string status, string? containerId = null, string? containerName = null, int? apiPort = null, int? wsPort = null, string? dockerUrl = null, string? errorMessage = null);
    Task UpdatePhoneNumberAsync(Guid phoneId, string phoneNumber);
    Task AssignPhoneToHostAsync(Guid phoneId, Guid hostId);
    Task LogAgentEventAsync(Guid? hostId, string eventType, object? eventData = null);

    // Contact operations
    Task<Contact?> GetContactByIdAsync(Guid contactId);
    Task<Contact?> GetContactByNumberAsync(Guid phoneId, string contactNumber);
    Task<List<Contact>> GetContactsForPhoneAsync(Guid phoneId);
    Task<Contact> CreateContactAsync(Contact contact);
    Task<Contact> UpdateContactAsync(Contact contact);
    Task<Contact> GetOrCreateContactAsync(Guid phoneId, string contactNumber, string? name = null);
    Task<Contact> UpsertContactAsync(Guid phoneId, string contactNumber, string? name = null, string? lid = null);

    // Message operations
    Task<Message?> GetMessageByIdAsync(Guid messageId);
    Task<List<Message>> GetMessagesForContactAsync(Guid contactId, int limit = 100);
    Task<List<Message>> GetMessagesForCallAsync(Guid callId, int limit = 100);
    Task<Message> CreateMessageAsync(Message message);
    //Task<Message> AddMessageAsync(Guid phoneId, Guid contactId, string sender, object content, bool direction, string? leafId = null);

    // Call operations
    Task<Call?> GetCallByIdAsync(Guid callId);
    Task<Call?> GetActiveCallForContactAsync(Guid phoneId, Guid contactId);
    Task<Call> GetOrCreateActiveCallAsync(Guid phoneId, Guid contactId);
    Task<Call> CreateCallAsync(Call call);
    Task<Call> UpdateCallAsync(Call call);

    Task UpdatePhoneCredsAsync(Guid phoneId, string credsBase64);

    Task<Message> AddMessageAsync(Guid phoneId, Guid contactId, string sender, object content, bool direction,
     string? leafId = null, string? whatsappMessageId = null);

     // PingSender operations
    Task<PingSender> CreatePingSenderAsync(Guid phoneId, string targetNumber, string? pingMessageId);
    Task<PingSender?> GetPendingPingSenderAsync(Guid phoneId, string targetNumber);
    Task<PingSender?> MatchPingSenderByLidAsync(Guid phoneId, string lid, Guid contactId);
}

public class SupabaseService : ISupabaseService
{
    private readonly Client _client;
    private readonly ILogger<SupabaseService> _logger;

    public SupabaseService(IConfiguration configuration, ILogger<SupabaseService> logger)
    {
        var url = Environment.GetEnvironmentVariable("SUPABASE_URL") 
            ?? throw new InvalidOperationException("SUPABASE_URL not set");
        var key = Environment.GetEnvironmentVariable("SUPABASE_KEY")
            ?? throw new InvalidOperationException("SUPABASE_KEY not set");
        _logger = logger;

        var options = new SupabaseOptions
        {
            AutoConnectRealtime = false
        };

        _client = new Client(url, key, options);
    }

    #region Host Operations

    public async Task<DbHost?> GetOrCreateHostAsync(string hostName, string ipAddress, string? externalIp, int portRangeStart, int portRangeEnd, int maxContainers)
    {
        try
        {
            DbHost? existingHost = null;

            // ── חפש לפי ExternalIp בלבד ──────────────────────────
            if (!string.IsNullOrEmpty(externalIp))
            {
                var response = await _client.From<DbHost>()
                    .Where(h => h.ExternalIp == externalIp)
                    .Get();
                existingHost = response.Models.FirstOrDefault();
            }

            if (existingHost != null)
            {
                // ── עדכן IpAddress (local) + HostName + heartbeat ──
                existingHost.IpAddress     = ipAddress;   // ← local IP מתעדכן
                existingHost.HostName      = hostName;
                existingHost.LastHeartbeat = DateTime.UtcNow;
                existingHost.Status        = "active";
                existingHost.PortRangeStart = portRangeStart;
                existingHost.PortRangeEnd   = portRangeEnd;
                existingHost.MaxContainers  = maxContainers;

                await _client.From<DbHost>().Update(existingHost);
                _logger.LogInformation("Updated existing host by ExternalIp {ExternalIp} → LocalIp {LocalIp}", externalIp, ipAddress);
                return existingHost;
            }

            // ── צור host חדש ─────────────────────────────────────
            var newHost = new DbHost
            {
                Id             = Guid.NewGuid(),
                HostName       = hostName,
                IpAddress      = ipAddress,    // ← local IP
                ExternalIp     = externalIp,   // ← external IP
                Status         = "active",
                LastHeartbeat  = DateTime.UtcNow,
                MaxContainers  = maxContainers,
                PortRangeStart = portRangeStart,
                PortRangeEnd   = portRangeEnd,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow
            };

            var insertResponse = await _client.From<DbHost>().Insert(newHost);
            _logger.LogInformation("Created new host: ExternalIp={ExternalIp} LocalIp={LocalIp}", externalIp, ipAddress);
            return insertResponse.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetOrCreateHostAsync for ExternalIp={ExternalIp}", externalIp);
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
                await _client.From<DbHost>().Update(host);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating heartbeat for host {HostId}", hostId);
        }
    }

    public async Task<List<DbHost>> GetActiveHostsAsync()
    {
        try
        {
            var response = await _client.From<DbHost>()
                .Where(h => h.Status == "active")
                .Get();

            return response.Models;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active hosts");
            return new List<DbHost>();
        }
    }

    public async Task<DbHost?> GetHostByIdAsync(Guid hostId)
    {
        try
        {
            var response = await _client.From<DbHost>()
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
                await _client.From<DbHost>().Update(host);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting host {HostId} status to {Status}", hostId, status);
        }
    }

    public async Task<List<DbHost>> GetDeadHostsAsync(int heartbeatTimeoutMinutes = 5)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-heartbeatTimeoutMinutes);
            var response = await _client.From<DbHost>()
                .Where(h => h.Status == "active")
                .Get();

            // סנן בצד הלקוח כי Supabase C# לא תומך ב-DateTime comparison ישירות
            return response.Models
                .Where(h => h.LastHeartbeat < cutoff)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dead hosts");
            return new List<DbHost>();
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

    #endregion

    #region Phone Operations

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

    public async Task<Phone?> GetPhoneByNumberAsync(string phoneNumber)
    {
        try
        {
            var response = await _client.From<Phone>()
                .Where(p => p.Number == phoneNumber)
                .Get();
            return response.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting phone by number {Number}", phoneNumber);
            return null;
        }
    }

    public async Task<Phone> CreatePhoneAsync(Phone phone)
    {
        try
        {
            var response = await _client.From<Phone>().Insert(phone);
            return response.Models.First();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating phone {Number}", phone.Number);
            throw;
        }
    }

    public async Task<List<Phone>> GetAllPhonesAsync()
    {
        try
        {
            var response = await _client.From<Phone>()
                .Where(p => p.Status == "active")
                .Get();
            return response.Models;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all phones");
            return new List<Phone>();
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

    public async Task LogAgentEventAsync(Guid? hostId, string eventType, object? eventData = null)
    {
        try
        {
            var evt = new AgentEvent
            {
                Id = Guid.NewGuid(),
                AgentHostId = hostId,
                EventType = eventType,
                EventData = eventData != null ? JsonSerializer.Serialize(eventData) : null,
                CreatedAt = DateTime.UtcNow
            };

            await _client.From<AgentEvent>().Insert(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging agent event {EventType}", eventType);
        }
    }

    #endregion

    #region Contact Operations

    public async Task<Contact?> GetContactByIdAsync(Guid contactId)
    {
        try
        {
            var response = await _client.From<Contact>()
                .Where(c => c.Id == contactId)
                .Get();
            return response.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting contact {ContactId}", contactId);
            return null;
        }
    }

    public async Task<Contact?> GetContactByNumberAsync(Guid phoneId, string contactNumber)
    {
        try
        {
            var response = await _client.From<Contact>()
                .Where(c => c.PhoneId == phoneId)
                .Where(c => c.Number == contactNumber)
                .Get();
            return response.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting contact by number {Number} for phone {PhoneId}", contactNumber, phoneId);
            return null;
        }
    }

    public async Task<List<Contact>> GetContactsForPhoneAsync(Guid phoneId)
    {
        try
        {
            var response = await _client.From<Contact>()
                .Where(c => c.PhoneId == phoneId)
                .Get();
            return response.Models;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting contacts for phone {PhoneId}", phoneId);
            return new List<Contact>();
        }
    }

    public async Task<Contact> CreateContactAsync(Contact contact)
    {
        try
        {
            contact.Id = Guid.NewGuid();
            var response = await _client.From<Contact>().Insert(contact);
            _logger.LogInformation("Created contact {ContactId} for phone {PhoneId}", contact.Id, contact.PhoneId);
            return response.Models.First();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating contact {Number}", contact.Number);
            throw;
        }
    }

    public async Task<Contact> UpdateContactAsync(Contact contact)
    {
        try
        {
            var response = await _client.From<Contact>().Update(contact);
            return response.Models.First();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating contact {ContactId}", contact.Id);
            throw;
        }
    }

    public async Task<Contact> GetOrCreateContactAsync(Guid phoneId, string contactNumber, string? name = null)
    {
        var existing = await GetContactByNumberAsync(phoneId, contactNumber);
        if (existing != null)
        {
            if (name != null && existing.Name != name)
            {
                existing.Name = name;
                return await UpdateContactAsync(existing);
            }
            return existing;
        }

        return await CreateContactAsync(new Contact
        {
            PhoneId = phoneId,
            Number = contactNumber,
            Name = name
        });
    }

    public async Task<Contact> UpsertContactAsync(Guid phoneId, string contactNumber, string? name = null, string? lid = null)
    {
        var existing = await GetContactByNumberAsync(phoneId, contactNumber);
        
        if (existing != null)
        {
            bool needsUpdate = false;
            
            if (!string.IsNullOrEmpty(name) && existing.Name != name)
            {
                existing.Name = name;
                needsUpdate = true;
            }
            
            if (!string.IsNullOrEmpty(lid) && existing.Lid != lid)
            {
                existing.Lid = lid;
                needsUpdate = true;
            }
            
            if (existing.IsConnect != true)
            {
                existing.IsConnect = true;
                needsUpdate = true;
            }
            
            if (needsUpdate)
            {
                _logger.LogDebug("Updating contact {ContactId} - Name: {Name}, Lid: {Lid}", 
                    existing.Id, name, lid);
                return await UpdateContactAsync(existing);
            }
            
            return existing;
        }

        _logger.LogInformation("Creating new contact {Number} for phone {PhoneId}", contactNumber, phoneId);
        return await CreateContactAsync(new Contact
        {
            PhoneId = phoneId,
            Number = contactNumber,
            Name = name,
            Lid = lid,
            IsConnect = true
        });
    }

    #endregion

    #region Phone Additional Operations

    public async Task UpdatePhoneNumberAsync(Guid phoneId, string phoneNumber)
    {
        try
        {
            var phone = await GetPhoneByIdAsync(phoneId);
            if (phone != null && phone.Number != phoneNumber)
            {
                phone.Number = phoneNumber;
                await _client.From<Phone>().Update(phone);
                _logger.LogInformation("Updated phone {PhoneId} number to {Number}", phoneId, phoneNumber);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating phone {PhoneId} number", phoneId);
        }
    }

    #endregion

    #region Message Operations

    public async Task<Message?> GetMessageByIdAsync(Guid messageId)
    {
        try
        {
            var response = await _client.From<Message>()
                .Where(m => m.Id == messageId)
                .Get();
            return response.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting message {MessageId}", messageId);
            return null;
        }
    }

    public async Task<List<Message>> GetMessagesForContactAsync(Guid contactId, int limit = 100)
    {
        try
        {
            var callResponse = await _client.From<Call>()
                .Where(c => c.ContactId == contactId)
                .Order(c => c.CreatedAt, Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(1)
                .Get();

            var call = callResponse.Models.FirstOrDefault();
            if (call == null)
            {
                return new List<Message>();
            }

            return await GetMessagesForCallAsync(call.Id, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting messages for contact {ContactId}", contactId);
            return new List<Message>();
        }
    }

    public async Task<List<Message>> GetMessagesForCallAsync(Guid callId, int limit = 100)
    {
        try
        {
            var response = await _client.From<Message>()
                .Where(m => m.CallId == callId)
                .Order(m => m.SentAt, Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(limit)
                .Get();
            return response.Models;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting messages for call {CallId}", callId);
            return new List<Message>();
        }
    }

    public async Task<Message> CreateMessageAsync(Message message)
    {
        try
        {
            message.Id = Guid.NewGuid();
            if (message.SentAt == null)
            {
                message.SentAt = DateTime.UtcNow;
            }
            var response = await _client.From<Message>().Insert(message);
            _logger.LogDebug("Created message {MessageId} for call {CallId}", message.Id, message.CallId);
            return response.Models.First();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating message");
            throw;
        }
    }
    public async Task<Message> AddMessageAsync(Guid phoneId, Guid contactId, string sender, object content,
    bool direction, string? leafId = null, string? whatsappMessageId = null)
    {
        var call = await GetOrCreateActiveCallAsync(phoneId, contactId);

        var message = new Message
        {
            CallId = call.Id,
            Sender = sender,
            Content = JsonSerializer.Serialize(content, new JsonSerializerOptions 
            { 
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
            }),
            Direction = direction,
            LeafId = leafId,
            WhatsappMessageId = whatsappMessageId,
            Status = "sent",
            RetryCounter = 0
        };

        return await CreateMessageAsync(message);
    }

    #endregion

    #region Call Operations

    public async Task<Call?> GetCallByIdAsync(Guid callId)
    {
        try
        {
            var response = await _client.From<Call>()
                .Where(c => c.Id == callId)
                .Get();
            return response.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting call {CallId}", callId);
            return null;
        }
    }

    public async Task<Call?> GetActiveCallForContactAsync(Guid phoneId, Guid contactId)
    {
        try
        {
            var response = await _client.From<Call>()
                .Where(c => c.PhoneId == phoneId)
                .Where(c => c.ContactId == contactId)
                .Where(c => c.Status == "active")
                .Order(c => c.CreatedAt, Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(1)
                .Get();
            return response.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active call for contact {ContactId}", contactId);
            return null;
        }
    }

    public async Task<Call> GetOrCreateActiveCallAsync(Guid phoneId, Guid contactId)
    {
        var existing = await GetActiveCallForContactAsync(phoneId, contactId);
        if (existing != null)
        {
            return existing;
        }

        return await CreateCallAsync(new Call
        {
            PhoneId = phoneId,
            ContactId = contactId,
            Status = "active",
            StartedAt = DateTime.UtcNow
        });
    }

    public async Task<Call> CreateCallAsync(Call call)
    {
        try
        {
            call.Id = Guid.NewGuid();
            var response = await _client.From<Call>().Insert(call);
            _logger.LogInformation("Created call {CallId} for phone {PhoneId} and contact {ContactId}", 
                call.Id, call.PhoneId, call.ContactId);
            return response.Models.First();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating call");
            throw;
        }
    }

    public async Task<Call> UpdateCallAsync(Call call)
    {
        try
        {
            var response = await _client.From<Call>().Update(call);
            return response.Models.First();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating call {CallId}", call.Id);
            throw;
        }
    }
    /// <summary>
/// Update phone creds_base64 after authentication
/// </summary>
public async Task UpdatePhoneCredsAsync(Guid phoneId, string credsBase64)
{
    try
    {
        var phone = await GetPhoneByIdAsync(phoneId);
        if (phone != null)
        {
          //  phone.CredsBase64 = credsBase64;
              phone.CredsBase64 = credsBase64;  // הסר את ה-//
            await _client.From<Phone>().Update(phone);
            _logger.LogInformation("Updated phone {PhoneId} creds_base64 (length: {Length})", phoneId, credsBase64.Length);
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error updating phone {PhoneId} creds", phoneId);
    }
}
 

    #endregion

    #region PingSender Operations

public async Task<PingSender> CreatePingSenderAsync(Guid phoneId, string targetNumber, string? pingMessageId)
{
    var pingSender = new PingSender
    {
        Id = Guid.NewGuid(),
        PhoneId = phoneId,
        TargetNumber = targetNumber,
        PingMessageId = pingMessageId,
        Status = "pending",
        CreatedAt = DateTime.UtcNow
    };

    var response = await _client.From<PingSender>().Insert(pingSender);
    _logger.LogInformation("Created PingSender {Id} for {TargetNumber}", pingSender.Id, targetNumber);
    return response.Models.First();
}

public async Task<PingSender?> GetPendingPingSenderAsync(Guid phoneId, string targetNumber)
{
    try
    {
        var response = await _client.From<PingSender>()
            .Where(p => p.PhoneId == phoneId)
            .Where(p => p.TargetNumber == targetNumber)
            .Where(p => p.Status == "pending")
            .Order(p => p.CreatedAt, Supabase.Postgrest.Constants.Ordering.Descending)
            .Limit(1)
            .Get();
        return response.Models.FirstOrDefault();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error getting pending PingSender");
        return null;
    }
}

public async Task<PingSender?> MatchPingSenderByLidAsync(Guid phoneId, string lid, Guid contactId)
{
    try
    {
        // מצא ping pending לפי מספר הטלפון של ה-contact
        var contact = await GetContactByIdAsync(contactId);
        if (contact == null) return null;

        var pingSender = await GetPendingPingSenderAsync(phoneId, contact.Number);
        if (pingSender == null) return null;

        // עדכן את ה-match
        pingSender.Lid = lid;
        pingSender.ContactId = contactId;
        pingSender.Status = "matched";
        pingSender.MatchedAt = DateTime.UtcNow;

        await _client.From<PingSender>().Update(pingSender);
        _logger.LogInformation("Matched PingSender {Id} with LID {Lid}", pingSender.Id, lid);
        return pingSender;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error matching PingSender by LID");
        return null;
    }
} 
   
#endregion
}