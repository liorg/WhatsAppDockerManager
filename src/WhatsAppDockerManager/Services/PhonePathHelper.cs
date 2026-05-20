namespace WhatsAppDockerManager.Services;

/// <summary>
/// Helper סטטי — חישוב שמות ונתיבים לכל מה שקשור ל-phone.
/// כל המחלקות (DockerService, ContainerManager, OrphanContainerCleanupService)
/// משתמשות כאן — שינוי במקום אחד מתפשט לכולם.
/// </summary>
public static class PhonePathHelper
{
    // ── Container name ────────────────────────────────────────────────
    /// <summary>
    /// whatsapp_972504476645_4a06a2a8
    /// ייחודי: מספר קריא + 8 תווים מה-GUID
    /// </summary>
    public static string ContainerName(string phoneNumber, Guid phoneId)
        => $"whatsapp_{phoneNumber.Replace("+", "")}_{phoneId.ToString("N")[..8]}";

    // ── Data paths ────────────────────────────────────────────────────
    /// <summary>/opt/whatsapp-data/auth_{phoneId}</summary>
    public static string AuthPath(string basePath, Guid phoneId)
        => Path.Combine(basePath, $"auth_{phoneId}");

    /// <summary>/opt/whatsapp-data/logs_{phoneId}</summary>
    public static string LogsPath(string basePath, Guid phoneId)
        => Path.Combine(basePath, $"logs_{phoneId}");

    /// <summary>/opt/whatsapp-data/contacts_{phoneId}</summary>
    public static string ContactsPath(string basePath, Guid phoneId)
        => Path.Combine(basePath, $"contacts_{phoneId}");

    /// <summary>
    /// מחזיר את כל 3 הנתיבים ביחד
    /// </summary>
    public static (string Auth, string Logs, string Contacts) AllPaths(string basePath, Guid phoneId)
        => (AuthPath(basePath, phoneId), LogsPath(basePath, phoneId), ContactsPath(basePath, phoneId));

    // ── Directory helpers ─────────────────────────────────────────────
    /// <summary>
    /// יוצר את 3 התיקיות אם לא קיימות (Linux בלבד)
    /// </summary>
    public static void EnsureDirectoriesExist(string basePath, Guid phoneId)
    {
        if (OperatingSystem.IsWindows()) return;
        var (auth, logs, contacts) = AllPaths(basePath, phoneId);
        Directory.CreateDirectory(auth);
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(contacts);
    }

    /// <summary>
    /// מוחק את 3 התיקיות אם קיימות
    /// </summary>
    public static void DeleteDirectories(string basePath, Guid phoneId)
    {
        var (auth, logs, contacts) = AllPaths(basePath, phoneId);
        TryDelete(auth);
        TryDelete(logs);
        TryDelete(contacts);
    }

    private static void TryDelete(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}