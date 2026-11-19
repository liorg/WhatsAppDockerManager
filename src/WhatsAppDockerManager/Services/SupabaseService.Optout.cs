// Services/SupabaseService.Optout.cs — opt-out check for SendController
namespace WhatsAppDockerManager.Services;

public partial interface ISupabaseService
{
    /// <summary>
    /// True when the recipient opted out (STOP / unsubscribe link) — blocked for every
    /// customer. Works for "<number>@s.whatsapp.net" and "<lid>@lid".
    /// </summary>
    Task<bool> IsOptedOutAsync(string jid);
}

public partial class SupabaseService
{
    public async Task<bool> IsOptedOutAsync(string jid)
    {
        if (string.IsNullOrWhiteSpace(jid)) return false;
        try
        {
            var res = await _client.Rpc("optout_is_blocked_jid", new Dictionary<string, object>
            {
                ["p_jid"] = jid,
            });
            return (res.Content ?? "").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // fail-open: a DB hiccup must not stop every send on the platform
            _logger.LogError(ex, "[OPTOUT] check failed for {Jid} — allowing send", jid);
            return false;
        }
    }
}
