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

    /// <summary>
    /// מחפש פורט פנוי שאין התנגשות עם הרשימה הקיימת.
    /// קרא את הפורטים הקיימים מ-DB לפני הקריאה.
    /// </summary>
    public static (int FastApi, int Baileys) GetBothPortsUnique(
        Guid phoneId,
        IEnumerable<int> usedFastApiPorts,
        IEnumerable<int> usedBaileysPorts,
        IConfiguration? config = null)
    {
        var usedFa = usedFastApiPorts.ToHashSet();
        var usedBa = usedBaileysPorts.ToHashSet();

        var fastApi = GetFastApiPort(phoneId, config);
        var baileys = GetBaileysPort(phoneId, config);

        // אם אין התנגשות — החזר כרגיל
        if (!usedFa.Contains(fastApi) && !usedBa.Contains(baileys))
            return (fastApi, baileys);

        // חפש פורט פנוי הקרוב ביותר
        for (int i = 0; i < 1000; i++)
        {
            var fa = FastApiRangeStart + ((fastApi - FastApiRangeStart + i) % 1000);
            var ba = BaileysRangeStart + ((baileys - BaileysRangeStart + i) % 1000);
            if (!usedFa.Contains(fa) && !usedBa.Contains(ba))
                return (fa, ba);
        }

        throw new InvalidOperationException("No available ports in range — all 1000 slots taken");
    }

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