// Models/WaTemplate.cs — קובץ חדש
//
// ה-Worker לא מחולל טקסט. הוא מעביר שם תבנית + פרמטרים,
// ומפענח {{payload...}} בערכי הפרמטרים בלבד.

using System.Text.Json.Serialization;

namespace WorkerScenarioRuntime.Models;

/// <summary>
/// מגיע מה-ICR: config.template של צעד send_message.
/// הערכים ב-Parameters עשויים להכיל {{payload...}} שטרם פוענח.
/// </summary>
public class WaTemplateConfig
{
    [JsonPropertyName("id")]   public string? Id   { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("lang")] public string? Lang { get; set; }

    /// <summary>מפתחות "header" / "body". ערך אחד לכל פרמטר, לפי סדר {{1}},{{2}}.</summary>
    [JsonPropertyName("parameters")]
    public Dictionary<string, List<string>>? Parameters { get; set; }
}

/// <summary>
/// מה שנשלח ל-Spine אחרי פענוח המשתנים. אין כאן טקסט הודעה —
/// ההודעה נבנית ב-HostAgent מתוך התבנית ב-DB.
/// </summary>
public class WaTemplatePayload
{
    [JsonPropertyName("id")]   public string? Id   { get; set; }
    [JsonPropertyName("name")] public string  Name { get; set; } = "";
    [JsonPropertyName("lang")] public string  Lang { get; set; } = "he";

    [JsonPropertyName("parameters")]
    public Dictionary<string, List<string>> Parameters { get; set; } = new();
}
