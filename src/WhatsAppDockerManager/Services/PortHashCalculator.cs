using System.Security.Cryptography;
using System.Text;

namespace WhatsAppDockerManager.Services;

/// <summary>
/// מחשב ports דטרמיניסטיים לפי מספר טלפון + secret seed.
/// כל קריאה עם אותו טלפון תחזיר תמיד אותו port — ללא DB.
/// </summary
public static class PortHashCalculator
{
    private const string FastApiSeed = "FASTAPI_PORT_SEED";
    private const string BaileysSeed = "BAILEYS_PORT_SEED";

    private const int FastApiRangeStart = 8000;
    private const int FastApiRangeEnd   = 8999;
    private const int BaileysRangeStart = 9000;
    private const int BaileysRangeEnd   = 9999;

    // ← חדש: לפי GUID (ייחודי per user+phone)
    public static int GetFastApiPort(Guid phoneId, IConfiguration? config = null)
    {
        var seed = config?["AppSettings:Ports:FastApiSeed"] ?? FastApiSeed;
        return ComputePort(phoneId.ToString("N"), seed, FastApiRangeStart, FastApiRangeEnd);
    }

    public static int GetBaileysPort(Guid phoneId, IConfiguration? config = null)
    {
        var seed = config?["AppSettings:Ports:BaileysSeed"] ?? BaileysSeed;
        return ComputePort(phoneId.ToString("N"), seed, BaileysRangeStart, BaileysRangeEnd);
    }

    public static (int FastApi, int Baileys) GetBothPorts(Guid phoneId, IConfiguration? config = null)
        => (GetFastApiPort(phoneId, config), GetBaileysPort(phoneId, config));

    // ── Overloads ישנים לתאימות לאחור ──────────────────────
    public static int GetFastApiPort(string phoneNumber, IConfiguration? config = null)
        => throw new InvalidOperationException(
            "Use GetFastApiPort(Guid phoneId) — port must be unique per phone record, not per number");

    public static int GetBaileysPort(string phoneNumber, IConfiguration? config = null)
        => throw new InvalidOperationException(
            "Use GetBaileysPort(Guid phoneId) — port must be unique per phone record, not per number");

    // ── Core ────────────────────────────────────────────────
    private static int ComputePort(string input, string seed, int rangeStart, int rangeEnd)
    {
        var keyBytes   = Encoding.UTF8.GetBytes(seed);
        var inputBytes = Encoding.UTF8.GetBytes(input);
        using var hmac = new HMACSHA256(keyBytes);
        var hash      = hmac.ComputeHash(inputBytes);
        var hashUint  = BitConverter.ToUInt32(hash, 0);
        var rangeSize = (uint)(rangeEnd - rangeStart + 1);
        return rangeStart + (int)(hashUint % rangeSize);
    }
}