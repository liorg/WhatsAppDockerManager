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
/// Canary traffic never reaches contacts/messages/Spine: the CHECK must not
/// create a draft contact or start a scenario, and the ACK's fromMe echo
/// must not either.
///
/// .env:
///   AppSettings__Canary__ProberNumbers=972501234567     comma-separated
///   AppSettings__Canary__AutoReply=true                 kill switch
/// </summary>
public static partial class CanaryAutoReply
{
    [GeneratedRegex(@"CHECK[\s\-_]*ID[\s:#\-]+([0-9A-Z]{6,16})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CheckRegex();

    [GeneratedRegex(@"^\s*ACK[\s:#\-]+[0-9A-Z]{6,16}\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AckRegex();

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
            return AckRegex().IsMatch(text ?? "") ? Kind.AckEcho : Kind.OtherFromProber;
        }

        if (string.Equals(cfg["AppSettings:Canary:AutoReply"], "false", StringComparison.OrdinalIgnoreCase))
            return Kind.OtherFromProber;

        var m = CheckRegex().Match(blob);
        if (!m.Success) return Kind.OtherFromProber;

        ack = $"ACK {m.Groups[1].Value.ToUpperInvariant()}";
        return Kind.Check;
    }

    public static bool IsProber(IConfiguration cfg, string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return false;
        return (cfg["AppSettings:Canary:ProberNumbers"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => SameNumber(p, number));
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