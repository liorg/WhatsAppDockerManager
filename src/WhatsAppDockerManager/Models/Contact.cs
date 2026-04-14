using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("contacts")]
public class Contact : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("phone_id")]
    public Guid? PhoneId { get; set; }

    [Column("lid")]
    public string? Lid { get; set; }

    [Column("number")]
    public string Number { get; set; } = string.Empty;

    [Column("name")]
    public string? Name { get; set; }

    [Column("email")]
    public string? Email { get; set; }

    [Column("avatar")]
    public string? Avatar { get; set; }

    [Column("tag")]
    public string? Tag { get; set; }

    [Column("is_bot")]
    public bool? IsBot { get; set; }

    [Column("is_connect")]
    public bool? IsConnect { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }
}
