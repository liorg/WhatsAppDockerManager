using System.Security.Cryptography;
using System.Text;

namespace WhatsAppDockerManager.Services;

/// <summary>
/// מחשב ports דטרמיניסטיים לפי מספר טלפון + secret seed.
/// כל קריאה עם אותו טלפון תחזיר תמיד אותו port — ללא DB.
/// </summary>
public static class PortHashCalculator
{
    // ── Secrets (עבור מניעת התנגשות בין שירותים) ────────────
    // בפרודקשן — קרא מ-environment variables
    private const string FastApiSeed = "FASTAPI_PORT_SEED";   // env: PORT_SEED_FASTAPI
    private const string BaileysSeed = "BAILEYS_PORT_SEED";   // env: PORT_SEED_BAILEYS

    // ── טווחי פורטים ────────────────────────────────────────
    // FastAPI: 8000–8999  (100 slots → עד 100 agents במקביל)
    // Baileys: 9000–9999  (100 slots → תמיד gap של 1000 מ-FastAPI)
    private const int FastApiRangeStart = 8000;
    private const int FastApiRangeEnd   = 8999;
    private const int BaileysRangeStart = 9000;
    private const int BaileysRangeEnd   = 9999;

    // ── Public API ───────────────────────────────────────────

    /// <summary>Port של FastAPI (Python) עבור מספר הטלפון</summary>
    public static int GetFastApiPort(string phoneNumber, IConfiguration? config = null)
    {
        var seed = config?["AppSettings:Ports:FastApiSeed"] ?? FastApiSeed;
        return ComputePort(phoneNumber, seed, FastApiRangeStart, FastApiRangeEnd);
    }

    /// <summary>Port של Baileys (Node.js) עבור מספר הטלפון</summary>
    public static int GetBaileysPort(string phoneNumber, IConfiguration? config = null)
    {
        var seed = config?["AppSettings:Ports:BaileysSeed"] ?? BaileysSeed;
        return ComputePort(phoneNumber, seed, BaileysRangeStart, BaileysRangeEnd);
    }

    /// <summary>מחזיר את שני הפורטים ביחד</summary>
    public static (int FastApi, int Baileys) GetBothPorts(string phoneNumber, IConfiguration? config = null)
        => (GetFastApiPort(phoneNumber, config), GetBaileysPort(phoneNumber, config));

    // ── Core hash logic ──────────────────────────────────────

    private static int ComputePort(string phoneNumber, string seed, int rangeStart, int rangeEnd)
    {
        // נקה את הטלפון — רק ספרות
        var normalized = NormalizePhone(phoneNumber);

        // HMACSHA256(seed, phoneNumber) → דטרמיניסטי + קשה לנחש
        var keyBytes   = Encoding.UTF8.GetBytes(seed);
        var inputBytes = Encoding.UTF8.GetBytes(normalized);

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(inputBytes);

        // קח 4 בתים ראשונים → uint → modulo range
        var hashUint  = BitConverter.ToUInt32(hash, 0);
        var rangeSize = (uint)(rangeEnd - rangeStart + 1);
        var offset    = (int)(hashUint % rangeSize);

        return rangeStart + offset;
    }

    private static string NormalizePhone(string phone)
        => new string(phone.Where(char.IsDigit).ToArray());
}