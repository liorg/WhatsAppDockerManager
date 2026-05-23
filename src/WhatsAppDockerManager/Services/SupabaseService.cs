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
    Task<Contact?> GetContactByLidAsync(Guid phoneId, string lid);
    Task<List<Contact>> GetContactsForPhoneAsync(Guid phoneId);
    Task<Contact> CreateContactAsync(Contact contact);
    Task<Contact> UpdateContactAsync(Contact contact);
    Task<Contact> GetOrCreateContactAsync(Guid phoneId, string contactNumber, string? name = null);
    Task<Contact> UpsertContactAsync(Guid phoneId, string contactNumber, string? name = null, string? lid = null);
    Task<Contact> CreateDraftContactAsync(Guid phoneId, string contactNumber, string? lid = null,
     string? name = null, Guid? userId = null);

    // Message operations
    Task<Message?> GetMessageByIdAsync(Guid messageId);
    Task<List<Message>> GetMessagesForContactAsync(Guid contactId, int limit = 100);
    Task<List<Message>> GetMessagesForCallAsync(Guid callId, int limit = 100);
    Task<Message> CreateMessageAsync(Message message);

    // Call operations
    Task<Call?> GetCallByIdAsync(Guid callId);
    Task<Call?> GetActiveCallForContactAsync(Guid phoneId, Guid contactId);
    Task<Call> GetOrCreateActiveCallAsync(Guid phoneId, Guid contactId);
    Task<Call> CreateCallAsync(Call call);
    Task<Call> UpdateCallAsync(Call call);

    Task UpdatePhoneCredsAsync(Guid phoneId, string credsBase64);

    Task<bool> MessageExistsAsync(string whatsappMessageId);
    Task<Message> AddMessageAsync(Guid phoneId, Guid contactId, string sender, object content, bool direction,
     string? leafId = null, string? whatsappMessageId = null);

    // PingSender operations
    Task<PingSender> CreatePingSenderAsync(Guid phoneId, string targetNumber, string? pingMessageId, Guid? userId = null);
    Task<PingSender?> GetPendingPingSenderAsync(Guid phoneId, string targetNumber);
    Task<PingSender?> GetLatestPendingPingSenderAsync(Guid phoneId);
    Task<PingSender?> MatchPingSenderByLidAsync(Guid phoneId, string lid, Guid contactId);
    Task UpdatePhoneUserIdAsync(Guid phoneId, Guid userId);
    Task DetachPhoneFromHostAsync(Guid phoneId);
    Task ClearPhoneForLogoutAsync(Guid phoneId);
    Task<(Phone phone, bool created)> GetOrCreatePhoneAsync(string phoneNumber, Guid userId, string? nickname = null);
    Task<PingSender?> GetPingSenderByMessageIdAsync(Guid phoneId, string pingMessageId);
    Task UpdatePingSenderAsync(PingSender pingSender);

    Task ClearPhoneCredsAsync(Guid phoneId);
    Task SetPhoneStatusAsync(Guid phoneId, string status);
    Task DeletePhoneAsync(Guid phoneId);
    Task<List<Phone>> GetPhonesByNumberAsync(string phoneNumber);

    Task UpdateAuthSessionAsync(Guid phoneId, string authSessionId);
    Task SetPhoneRevisionAsync(Guid phoneId, int revision);

     Task<int> GetMaxRevisionByNumberAsync(string phoneNumber);
     Task<int> IncrementPhoneRevisionAsync(Guid phoneId);
    Task<string> GetMaskedUsernameAsync(Guid userId);
}

public class SupabaseService : ISupabaseService
{
    private readonly Client _client;
    private readonly ILogger<SupabaseService> _logger;

    // ── JsonSerializerOptions משותף לכל השירות ──────────────────────────────
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

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
    
public async Task SetPhoneRevisionAsync(Guid phoneId, int revision)
{
    var phone = await GetPhoneByIdAsync(phoneId);
    if (phone == null) return;
    phone.AuthRevision = revision;
    await _client.From<Phone>().Update(phone);
    _logger.LogInformation("[AUTH] Phone {PhoneId} AuthRevision → {Rev}", phoneId, revision);
} 
public async Task<int> GetMaxRevisionByNumberAsync(string phoneNumber)
{
    try
    {
        var clean = new string(phoneNumber.Where(char.IsDigit).ToArray());
        var response = await _client.From<Phone>()
            .Filter("number",
                Supabase.Postgrest.Constants.Operator.In,
                new List<string> { clean, "+" + clean })
            .Get();

        var max = response.Models
            .Select(p => p.AuthRevision)
            .DefaultIfEmpty(0)
            .Max();

        _logger.LogInformation("[AUTH] Max revision for {Number} = {Max}", phoneNumber, max);
        return max;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "[AUTH] Error getting max revision for {Number}", phoneNumber);
        return 0;
    }
}



public async Task<string> GetMaskedUsernameAsync(Guid userId)
{
    try
    {
        // שלוף email דרך טבלת phones — user_id קיים שם
        var response = await _client
            .Rpc("get_user_email", new { user_id = userId.ToString() })
            .ExecuteAsync<string>();

        var email     = response ?? "";
        var localPart = email.Contains('@') ? email.Split('@')[0] : email;
        var limited   = localPart.Length > 8 ? localPart[..8] : localPart;
        var masked    = limited.Length <= 4
            ? new string('*', 4) + limited
            : "****" + limited[^4..];

        return masked;
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "[AUTH] Failed to get username for {UserId}", userId);
        return "****" + userId.ToString("N")[..4];
    }
}

public async Task<int> IncrementPhoneRevisionAsync(Guid phoneId)
{
    var phone = await GetPhoneByIdAsync(phoneId);
    if (phone == null) return 0;
    
    phone.AuthRevision += 1;
    await _client.From<Phone>().Update(phone);
    
    _logger.LogInformation("[AUTH] Phone {PhoneId} revision → {Revision}", 
        phoneId, phone.AuthRevision);
    return phone.AuthRevision;
}

    public async Task<DbHost?> GetOrCreateHostAsync(string hostName, string ipAddress, string? externalIp, int portRangeStart, int portRangeEnd, int maxContainers)
    {
        try
        {
            DbHost? existingHost = null;

            if (!string.IsNullOrEmpty(externalIp))
            {
                var response = await _client.From<DbHost>()
                    .Where(h => h.ExternalIp == externalIp)
                    .Get();
                existingHost = response.Models.FirstOrDefault();
            }

            if (existingHost != null)
            {
                existingHost.IpAddress      = ipAddress;
                existingHost.HostName       = hostName;
                existingHost.LastHeartbeat  = DateTime.UtcNow;
                existingHost.Status         = "active";
                existingHost.PortRangeStart = portRangeStart;
                existingHost.PortRangeEnd   = portRangeEnd;
                existingHost.MaxContainers  = maxContainers;

                await _client.From<DbHost>().Update(existingHost);
                _logger.LogInformation("Updated existing host by ExternalIp {ExternalIp} → LocalIp {LocalIp}", externalIp, ipAddress);
                return existingHost;
            }

            var newHost = new DbHost
            {
                Id             = Guid.NewGuid(),
                HostName       = hostName,
                IpAddress      = ipAddress,
                ExternalIp     = externalIp,
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
                    return port;
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

    public async Task UpdateAuthSessionAsync(Guid phoneId, string authSessionId)
{
    await _client
        .From<Phone>()
        .Where(p => p.Id == phoneId)
        .Set(p => p.AuthSessionId, authSessionId)
        .Update();
}

    public async Task ClearPhoneCredsAsync(Guid phoneId)
{
    try
    {
        await _client.From<Phone>()
            .Where(p => p.Id == phoneId)
            .Set(p => p.CredsBase64, (string?)null)
            .Set(p => p.DockerStatus, PhoneDockerStatus.Pending)
            .Set(p => p.ErrorMessage, (string?)null)
            .Update();

        _logger.LogInformation("[PROVISION] Cleared creds for phone {PhoneId}", phoneId);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error clearing creds for phone {PhoneId}", phoneId);
    }
}

    public async Task<PingSender?> GetPingSenderByMessageIdAsync(Guid phoneId, string pingMessageId)
    {
        try
        {
            var response = await _client.From<PingSender>()
                .Where(p => p.PhoneId == phoneId)
                .Where(p => p.PingMessageId == pingMessageId)
                .Limit(1)
                .Get();
            return response.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting PingSender by messageId {MsgId}", pingMessageId);
            return null;
        }
    }

    public async Task UpdatePingSenderAsync(PingSender pingSender)
    {
        try
        {
            await _client.From<PingSender>().Update(pingSender);
            _logger.LogInformation("Updated PingSender {Id}", pingSender.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating PingSender {Id}", pingSender.Id);
            throw;
        }
    }

    public async Task<PingSender?> MatchPingSenderByLidAsync(Guid phoneId, string lid, Guid contactId)
    {
        try
        {
            var ps = await GetLatestPendingPingSenderAsync(phoneId);
            if (ps == null)
            {
                _logger.LogInformation("[MATCH] No pending ping_sender for phone {PhoneId}", phoneId);
                return null;
            }

            var newContactId = ps.ContactId;

            ps.Lid       = lid;
            ps.Status    = "matched";
            ps.MatchedAt = DateTime.UtcNow;
            await _client.From<PingSender>().Update(ps);

            var draftContact = await GetContactByIdAsync(contactId);
            if (draftContact != null)
            {
                await _client.From<Contact>().Update(draftContact);
            }

            _logger.LogInformation("[MATCH] draft={ContactId} (LID={Lid}) ↔ ping_sender={PsId}",
                contactId, lid, ps.Id);
            return ps;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MATCH] Error for phone {PhoneId}", phoneId);
            return null;
        }
    }

    public async Task<PingSender?> GetLatestPendingPingSenderAsync(Guid phoneId)
    {
        try
        {
            var response = await _client.From<PingSender>()
                .Where(p => p.PhoneId == phoneId)
                .Where(p => p.Status == "pending")
                .Order(p => p.CreatedAt, Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(1)
                .Get();
            return response.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting latest pending PingSender for phone {PhoneId}", phoneId);
            return null;
        }
    }

    public async Task<(Phone phone, bool created)> GetOrCreatePhoneAsync(
        string phoneNumber, Guid userId, string? nickname = null)
    {
        var clean    = new string(phoneNumber.Where(char.IsDigit).ToArray());
        var withPlus = "+" + clean;

        try
        {
            var existing = await _client.From<Phone>()
                .Where(p => p.UserId == userId)
                .Where(p => p.Number == clean || p.Number == withPlus)
                .Limit(1)
                .Get();

            if (existing.Models.Any())
            {
                var phone = existing.Models.First();
                _logger.LogInformation("[PROVISION] Phone {Number} already exists for user {UserId} → id={Id}",
                    clean, userId, phone.Id);
                return (phone, false);
            }

            var newPhone = new Phone
            {
                Id           = Guid.NewGuid(),
                Number       = withPlus,
                UserId       = userId,
                Label        = nickname,
                Status       = "active",
                DockerStatus = PhoneDockerStatus.Pending,
                CreatedAt    = DateTime.UtcNow,
            };

            var response = await _client.From<Phone>().Insert(newPhone);
            var created  = response.Models.First();

            _logger.LogInformation("[PROVISION] Created new phone {Number} for user {UserId} → id={Id}",
                clean, userId, created.Id);
            return (created, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PROVISION] Error in GetOrCreatePhoneAsync for {Number}", phoneNumber);
            throw;
        }
    }

    public async Task DetachPhoneFromHostAsync(Guid phoneId)
    {
        try
        {
            var phone = await GetPhoneByIdAsync(phoneId);
            if (phone != null)
            {
                phone.HostId        = null;
                phone.ContainerId   = null;
                phone.ContainerName = null;
                phone.DockerUrl     = null;
                phone.DockerStatus  = PhoneDockerStatus.Stopped;
                await _client.From<Phone>().Update(phone);
                _logger.LogInformation("Detached phone {PhoneId} from host", phoneId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detaching phone {PhoneId} from host", phoneId);
        }
    }

    public async Task UpdatePhoneUserIdAsync(Guid phoneId, Guid userId)
    {
        try
        {
            var phone = await GetPhoneByIdAsync(phoneId);
            if (phone != null)
            {
                phone.UserId = userId;
                await _client.From<Phone>().Update(phone);
                _logger.LogInformation("Updated phone {PhoneId} user_id to {UserId}", phoneId, userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating phone {PhoneId} user_id", phoneId);
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
            var clean = new string(phoneNumber.Where(char.IsDigit).ToArray());
            var response = await _client.From<Phone>()
                .Where(p => p.Number == clean || p.Number == "+" + clean)
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

    public async Task UpdatePhoneDockerStatusAsync(Guid phoneId, string status,
        string? containerId = null, string? containerName = null,
        int? apiPort = null, int? wsPort = null,
        string? dockerUrl = null, string? errorMessage = null)
    {
        _logger.LogWarning("ENTER UpdatePhoneDockerStatusAsync");
        try
        {
            var phone = await GetPhoneByIdAsync(phoneId);
            if (phone == null)
            {
                _logger.LogError("PHONE NULL");
                return;
            }
            _logger.LogWarning("PHONE FOUND");

            phone.DockerStatus    = status;
            phone.LastHealthCheck = DateTime.UtcNow;

            if (containerId    != null) phone.ContainerId   = containerId;
            if (containerName  != null) phone.ContainerName = containerName;
            if (apiPort        != null) phone.ApiPort       = apiPort;
            if (wsPort         != null) phone.WsPort        = wsPort;
            if (dockerUrl      != null) phone.DockerUrl     = dockerUrl;
            if (errorMessage   != null) phone.ErrorMessage  = errorMessage;

            if (status == PhoneDockerStatus.Running)
                phone.ErrorMessage = null;

            await _client.From<Phone>().Update(phone);
            _logger.LogWarning("UPDATE DONE");
            _logger.LogDebug("Updated phone {PhoneId} docker status to {Status}", phoneId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating phone {PhoneId} docker status", phoneId);
            throw;
        }
    }

    public async Task AssignPhoneToHostAsync(Guid phoneId, Guid hostId)
    {
        try
        {
            var phone = await GetPhoneByIdAsync(phoneId);
            if (phone != null)
            {
                phone.HostId       = hostId;
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
                Id          = Guid.NewGuid(),
                AgentHostId = hostId,
                EventType   = eventType,
                EventData   = eventData != null ? JsonSerializer.Serialize(eventData, _jsonOptions) : null,
                CreatedAt   = DateTime.UtcNow
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

    public async Task<Contact?> GetContactByLidAsync(Guid phoneId, string lid)
    {
        try
        {
            var cleanLid = lid.Contains('@') ? lid.Split('@')[0] : lid;
            var response = await _client.From<Contact>()
                .Where(c => c.PhoneId == phoneId)
                .Where(c => c.Lid == cleanLid)
                .Get();
            return response.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting contact by LID {Lid} for phone {PhoneId}", lid, phoneId);
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
            Number  = contactNumber,
            Name    = name
        });
    }

    public async Task<Contact> UpsertContactAsync(Guid phoneId, string contactNumber, string? name = null, string? lid = null)
    {
        var existing = await GetContactByNumberAsync(phoneId, contactNumber);

        if (existing != null)
        {
            bool needsUpdate = false;

            if (!string.IsNullOrEmpty(name) && existing.Name != name)
            { existing.Name = name; needsUpdate = true; }

            if (!string.IsNullOrEmpty(lid) && string.IsNullOrEmpty(existing.Lid))
            {
                existing.Lid = lid;
                needsUpdate  = true;
                _logger.LogInformation("[UPSERT] Linking contact {ContactId} with LID {Lid}", existing.Id, lid);
            }
            else if (!string.IsNullOrEmpty(lid) && existing.Lid != lid)
            {
                _logger.LogWarning("[UPSERT] Contact {ContactId} already has LID={ExistingLid}, ignoring new LID={NewLid}",
                    existing.Id, existing.Lid, lid);
            }

            if (existing.IsConnect != true)
            { existing.IsConnect = true; needsUpdate = true; }

            if (needsUpdate)
            {
                _logger.LogDebug("Updating contact {ContactId} - Name: {Name}, Lid: {Lid}", existing.Id, name, lid);
                return await UpdateContactAsync(existing);
            }

            return existing;
        }

        _logger.LogInformation("Creating new contact {Number} for phone {PhoneId}", contactNumber, phoneId);
        return await CreateContactAsync(new Contact
        {
            PhoneId   = phoneId,
            Number    = contactNumber,
            Name      = name,
            Lid       = lid,
            IsConnect = true
        });
    }

    public async Task<Contact> CreateDraftContactAsync(
        Guid phoneId, string contactNumber, string? lid = null,
        string? name = null, Guid? userId = null)
    {
        var cleanLid = lid?.Contains('@') == true ? lid.Split('@')[0] : lid;

        // ── 1. חפש לפי LID ────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(cleanLid))
        {
            var byLid = await GetContactByLidAsync(phoneId, cleanLid);
            if (byLid != null)
            {
                _logger.LogInformation("[DRAFT] Contact with LID={Lid} already exists: {Id}", cleanLid, byLid.Id);

                bool needsUpdate = false;
                if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(byLid.WhatsappName))
                { byLid.WhatsappName = name; needsUpdate = true; }
                if (!string.IsNullOrEmpty(name) && (string.IsNullOrEmpty(byLid.Name) || byLid.Name == byLid.Number))
                { byLid.Name = name; needsUpdate = true; }
                if (needsUpdate)
                    return await UpdateContactAsync(byLid);
                return byLid;
            }
        }

        // ── 2. חפש לפי number ─────────────────────────────────────────────
        var byNumber = await GetContactByNumberAsync(phoneId, contactNumber);
        if (byNumber != null)
        {
            _logger.LogInformation("[DRAFT] Contact with number={Number} already exists: {Id}", contactNumber, byNumber.Id);

            bool needsUpdate = false;
            if (!string.IsNullOrEmpty(cleanLid) && string.IsNullOrEmpty(byNumber.Lid))
            { byNumber.Lid = cleanLid; needsUpdate = true; }
            if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(byNumber.WhatsappName))
            { byNumber.WhatsappName = name; needsUpdate = true; }
            if (needsUpdate)
                return await UpdateContactAsync(byNumber);
            return byNumber;
        }

        // ── 3. צור חדש ────────────────────────────────────────────────────
        try
        {
            return await CreateContactAsync(new Contact
            {
                PhoneId      = phoneId,
                Number       = contactNumber,
                Lid          = cleanLid,
                Name         = name ?? contactNumber,
                WhatsappName = name,
                Tag          = "draft",
                IsConnect    = false,
                CreatedAt    = DateTime.UtcNow,
                UserId       = userId
            });
        }
        catch (Exception ex) when (ex.Message.Contains("23505") ||
                                   ex.Message.Contains("duplicate key") ||
                                   ex.Message.Contains("unique constraint"))
        {
            _logger.LogWarning("[DRAFT] Duplicate key on insert for LID={Lid} number={Number} — fetching existing",
                cleanLid, contactNumber);

            if (!string.IsNullOrEmpty(cleanLid))
            {
                var existing = await GetContactByLidAsync(phoneId, cleanLid);
                if (existing != null) return existing;
            }

            var existingByNum = await GetContactByNumberAsync(phoneId, contactNumber);
            if (existingByNum != null) return existingByNum;

            throw;
        }
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
                return new List<Message>();

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
                message.SentAt = DateTime.UtcNow;

            var response = await _client.From<Message>().Insert(message);
            _logger.LogDebug("Created message {MessageId} for contact {ContactId}", message.Id, message.ContactId);
            return response.Models.First();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating message");
            throw;
        }
    }

    public async Task<bool> MessageExistsAsync(string whatsappMessageId)
    {
        try
        {
            var result = await _client.From<Message>()
                .Where(m => m.WhatsappMessageId == whatsappMessageId)
                .Limit(1)
                .Get();
            return result.Models.Any();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking message existence {MsgId}", whatsappMessageId);
            return false;
        }
    }

    /// <summary>
    /// שומר הודעה עם content מלא — תומך ב-text, list_message, buttons.
    /// content מגיע כ-Dictionary שיכול להכיל JsonElement (מה-webhook payload).
    /// JsonSerializer מטפל ב-JsonElement אוטומטית.
    /// </summary>
    public async Task<Message> AddMessageAsync(
        Guid phoneId, Guid contactId, string sender, object content,
        bool direction, string? leafId = null, string? whatsappMessageId = null)
    {
        string contentJson;

        try
        {
            // JsonSerializer מסריאלז JsonElement בתוך Dictionary בצורה נכונה
            contentJson = JsonSerializer.Serialize(content, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MSG] Failed to serialize content — falling back to empty object");
            contentJson = "{}";
        }

        _logger.LogDebug("[MSG] Saving content: {Content}", contentJson);

        var message = new Message
        {
            CallId            = null,
            PhoneId           = phoneId,
            ContactId         = contactId,
            Sender            = sender,
            Content           = contentJson,
            Direction         = direction,
            LeafId            = leafId,
            WhatsappMessageId = whatsappMessageId,
            Status            = "sent",
            RetryCounter      = 0
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
            return existing;

        return await CreateCallAsync(new Call
        {
            PhoneId   = phoneId,
            ContactId = contactId,
            Status    = "active",
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

    public async Task UpdatePhoneCredsAsync(Guid phoneId, string credsBase64)
    {
        try
        {
            var phone = await GetPhoneByIdAsync(phoneId);
            if (phone != null)
            {
                phone.CredsBase64 = credsBase64;
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

    public async Task<PingSender> CreatePingSenderAsync(Guid phoneId, string targetNumber, string? pingMessageId, Guid? userId = null)
    {
        var pingSender = new PingSender
        {
            Id            = Guid.NewGuid(),
            PhoneId       = phoneId,
            TargetNumber  = targetNumber,
            PingMessageId = pingMessageId,
            Status        = "pending",
            CreatedAt     = DateTime.UtcNow,
            UserId        = userId
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

    public async Task ClearPhoneForLogoutAsync(Guid phoneId)
    {
        var phone = await GetPhoneByIdAsync(phoneId);
        if (phone == null) return;

        phone.Status          = "disconnected";
        phone.DockerStatus    = PhoneDockerStatus.Pending;
        phone.ContainerId     = null;
        phone.ContainerName   = null;
        phone.DockerUrl       = null;
        phone.ErrorMessage    = null;
        phone.CredsBase64     = null;
        phone.LastHealthCheck = DateTime.UtcNow;

        await _client.From<Phone>().Update(phone);
        _logger.LogInformation("Cleared phone {PhoneId} for logout", phoneId);
    }

    #endregion

    public async Task SetPhoneStatusAsync(Guid phoneId, string status)
    {
        try
        {
            await _client.From<Phone>()
                .Where(p => p.Id == phoneId)
                .Set(p => p.Status, status)
                .Update();
            _logger.LogInformation("[DB] Phone {PhoneId} status → {Status}", phoneId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DB] Error setting phone {PhoneId} status to {Status}", phoneId, status);
        }
    }

    public async Task DeletePhoneAsync(Guid phoneId)
    {
        try
        {
            await _client.From<Phone>()
                .Where(p => p.Id == phoneId)
                .Delete();
            _logger.LogInformation("[DB] Phone {PhoneId} deleted", phoneId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DB] Error deleting phone {PhoneId}", phoneId);
        }
    }

public async Task<List<Phone>> GetPhonesByNumberAsync(string phoneNumber)
{
    try
    {
        _logger.LogInformation("[DB] Getting phones by number {Number}", phoneNumber);

        var clean = new string(phoneNumber.Where(char.IsDigit).ToArray());

        var response = await _client
            .From<Phone>()
            .Filter("number",
                Supabase.Postgrest.Constants.Operator.In,
                new List<string> { clean, "+" + clean })
            .Get();

        // ← כאן
        _logger.LogInformation(
            "[DB] Found {Count} phones",
            response.Models?.Count ?? 0);

        foreach (var p in response.Models ?? [])
        {
            _logger.LogInformation(
                "[DB] Match: {Id} {Number} {Status}",
                p.Id,
                p.Number,
                p.Status);
        }

        return response.Models ?? new List<Phone>();
    }
    catch (Exception ex)
    {
        _logger.LogError(
            ex,
            "[DB] Error getting phones by number {Number}",
            phoneNumber);

        return new List<Phone>();
    }
}
     public async Task<List<Phone>> GetPhonesByNumberAsyncx(string phoneNumber)
    {
        try
        {
            _logger.LogInformation("[DB] Getting phones by number {Number}", phoneNumber);  

            var clean = new string(phoneNumber.Where(char.IsDigit).ToArray());
            var response = await _client.From<Phone>()
                .Where(p => p.Number == clean || p.Number == "+" + clean)
                .Get();
            return response.Models ?? new List<Phone>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DB] Error getting phones by number {Number}", phoneNumber);
            return new List<Phone>();
        }
    }
}
