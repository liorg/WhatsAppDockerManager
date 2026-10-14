using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhatsAppDockerManager.Services;

/// <summary>
/// Canary v2 - the monitored phone's half of the handshake.
///
/// The prober (Cloud API, in the whqueue stack) sends every active phone the
/// check_id template: "... CHECK ID: 7F3K2Q9A". The phone answers
/// "ACK 7F3K2Q9A" from its own container, and PhoneOrchestrator closes the
/// check by that code. Answering here, on the real inbound path, is the
/// point: an ACK proves inbound (container → Manager) and outbound
/// (Manager → container → WhatsApp) both work for that phone.
///
/// Canary traffic is saved to messages (with a draft contact for the prober)
/// so the exchange is visible, but never dispatched to webhooks: the CHECK
/// must not reach a customer's endpoint or start a scenario.
///
/// .env — אחד משני השמות, מה שתואם לשאר ה-.env:
///   AppSettings__Canary__ProberNumbers=972501234567   (או CANARY_PROBER_NUMBERS=…)  comma-separated
///   AppSettings__Canary__AutoReply=false              (או CANARY_AUTO_REPLY=false)  kill switch
///   AppSettings__Canary__SaveMessages=false           (או CANARY_SAVE_MESSAGES=false) לא לשמור ל-messages
/// </summary>
public static class CanaryAutoReply
{
    private static readonly Regex Check =
        new(@"CHECK[\s\-_]*ID[\s:#\-]+([0-9A-Z]{6,16})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Ack =
        new(@"^\s*ACK[\s:#\-]+[0-9A-Z]{6,16}\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public enum Kind { None, Check, AckEcho, OtherFromProber }

    /// <summary>
    /// Classifies a message on the prober's conversation.
    ///   Check            incoming CHECK → answer with <paramref name="ack"/>, do not save
    ///   AckEcho          our own ACK coming back as fromMe → drop
    ///   OtherFromProber  anything else with the prober → drop (no contact for it)
    ///   None             not canary traffic → normal flow
    /// The whole data dict is scanned, not just "text": how a template lands
    /// in Baileys (text / templateMessage / hydrated…) varies by version.
    /// </summary>
    public static Kind Classify(
        IConfiguration cfg, string? peerNumber, bool isIncoming,
        IDictionary<string, object>? data, out string ack)
    {
        ack = "";
        if (!IsProber(cfg, peerNumber)) return Kind.None;

        var blob = data == null ? "" : JsonSerializer.Serialize(data);

        if (!isIncoming)
        {
            var text = data != null && data.TryGetValue("text", out var t) ? t?.ToString() : null;
            return Ack.IsMatch(text ?? "") ? Kind.AckEcho : Kind.OtherFromProber;
        }

        if (string.Equals(Get(cfg, "AppSettings:Canary:AutoReply", "CANARY_AUTO_REPLY"), "false",
                          StringComparison.OrdinalIgnoreCase))
            return Kind.OtherFromProber;

        var m = Check.Match(blob);
        if (!m.Success) return Kind.OtherFromProber;

        ack = $"ACK {m.Groups[1].Value.ToUpperInvariant()}";
        return Kind.Check;
    }

    /// <summary>
    /// Save CHECK/ACK to messages (and a draft contact for the prober) so the
    /// exchange is visible in the UI. Never dispatched to webhooks.
    /// Off: AppSettings__Canary__SaveMessages=false (or CANARY_SAVE_MESSAGES=false).
    /// </summary>
    public static bool SaveMessages(IConfiguration cfg) =>
        !string.Equals(Get(cfg, "AppSettings:Canary:SaveMessages", "CANARY_SAVE_MESSAGES"), "false",
                       StringComparison.OrdinalIgnoreCase);

    public static bool IsProber(IConfiguration cfg, string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return false;
        return (Get(cfg, "AppSettings:Canary:ProberNumbers", "CANARY_PROBER_NUMBERS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => SameNumber(p, number));
    }

    /// <summary>
    /// AppSettings__Canary__X מגיע דרך IConfiguration; שם שטוח (CANARY_X) נקרא
    /// ישירות מה-env, למקרה שה-.env נטען בלי מיפוי ל-AppSettings.
    /// </summary>
    private static string? Get(IConfiguration cfg, string key, string flatEnv)
    {
        var v = cfg[key];
        if (!string.IsNullOrWhiteSpace(v)) return v;
        v = cfg[flatEnv];
        return !string.IsNullOrWhiteSpace(v) ? v : Environment.GetEnvironmentVariable(flatEnv);
    }

    /// <summary>Last 9 digits - survives +, leading 0 and @s.whatsapp.net. Never matches a @lid.</summary>
    public static bool SameNumber(string? a, string? b)
    {
        if ((a ?? "").EndsWith("@lid") || (b ?? "").EndsWith("@lid")) return false;
        static string D(string? s) => new((s ?? "").Split('@')[0].Where(char.IsDigit).ToArray());
        var x = D(a); var y = D(b);
        if (x.Length < 7 || y.Length < 7) return false;
        var n = Math.Min(9, Math.Min(x.Length, y.Length));
        return x[^n..] == y[^n..];
    }
}