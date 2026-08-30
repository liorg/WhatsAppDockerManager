// Models/PhoneTemplate.cs — קובץ חדש
//
// ממופה לטבלת phone_templates. עמודת content היא jsonb וממופה
// לטיפוס מורכב; JsonPropertyName מכסה כל PropertyNamingPolicy.

using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("phone_templates")]
public class PhoneTemplate : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("phone_id")]
    public Guid PhoneId { get; set; }

    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("lang")]
    public string Lang { get; set; } = "he";

    [Column("category")]
    public string Category { get; set; } = "UTILITY";

    /// <summary>pending | approved | rejected | pause</summary>
    [Column("status")]
    public string Status { get; set; } = "pending";

    [Column("is_published")]
    public bool IsPublished { get; set; }

    [Column("param_count")]
    public int ParamCount { get; set; }

    [Column("content")]
    public TemplateContent? Content { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}


// ── מבנה ה-jsonb של content ─────────────────────────────────────────────────

public class TemplateContent
{
    [JsonPropertyName("header")]  public TemplateHeader?       Header  { get; set; }
    [JsonPropertyName("body")]    public TemplatePart?         Body    { get; set; }
    [JsonPropertyName("footer")]  public TemplatePart?         Footer  { get; set; }
    [JsonPropertyName("buttons")] public List<TemplateButton>? Buttons { get; set; }
}

public class TemplatePart
{
    [JsonPropertyName("text")] public string? Text { get; set; }
}

public class TemplateHeader : TemplatePart
{
    /// <summary>none | text | image | video | document</summary>
    [JsonPropertyName("format")] public string? Format { get; set; }
}

public class TemplateButton
{
    /// <summary>quick_reply</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
}
