using System.Text.Json;
using System.Text.RegularExpressions;
using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services;

/// <summary>
/// המרות בין פורמט התבנית של הספק (components[]) לייצוג הפנימי.
/// שכבת דומיין — נצרכת גם ע"י ה-Controller וגם ע"י ה-BackgroundService.
/// </summary>
public static class TemplateMapper
{
    private static readonly Regex ParamRe = new(@"\{\{\s*(\d+)\s*\}\}", RegexOptions.Compiled);

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>WhatsApp components[] → TemplateContent {header, body, footer, buttons}</summary>
    public static TemplateContent ToTemplateContent(JsonElement body)
    {
        var content = new TemplateContent();
        if (!body.TryGetProperty("components", out var comps) || comps.ValueKind != JsonValueKind.Array)
            return content;

        foreach (var comp in comps.EnumerateArray())
        {
            var text = Str(comp, "text");
            switch (Str(comp, "type")?.ToUpperInvariant())
            {
                case "HEADER":
                    content.Header = new TemplateHeader { Text = text, Format = Str(comp, "format")?.ToLowerInvariant() ?? "text" };
                    break;
                case "BODY":
                    content.Body = new TemplatePart { Text = text };
                    break;
                case "FOOTER":
                    content.Footer = new TemplatePart { Text = text };
                    break;
                case "BUTTONS":
                    if (comp.TryGetProperty("buttons", out var btns) && btns.ValueKind == JsonValueKind.Array)
                        content.Buttons = btns.EnumerateArray()
                            .Select(b => new TemplateButton { Type = Str(b, "type")?.ToLowerInvariant(), Text = Str(b, "text") })
                            .ToList();
                    break;
            }
        }
        return content;
    }

    /// <summary>כמה ערכים השיגור ידרוש — זהה ל-SendController.MaxParamIndex</summary>
    public static int MaxParamIndex(string? text) =>
        string.IsNullOrEmpty(text) ? 0
            : ParamRe.Matches(text).Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0).DefaultIfEmpty(0).Max();

    /// <summary>PENDING/APPROVED/REJECTED/PAUSED → pending | approved | rejected | pause</summary>
    public static string MapStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "APPROVED" => "approved",
        "REJECTED" => "rejected",
        "PAUSED"   => "pause",
        _          => "pending",
    };
}
