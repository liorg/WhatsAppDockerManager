using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace WhatsAppDockerManager.Models;

[Table("calls")]
public class Call : BaseModel
{
    [PrimaryKey("id")]
    public Guid Id { get; set; }

    [Column("phone_id")]
    public Guid? PhoneId { get; set; }

    [Column("contact_id")]
    public Guid? ContactId { get; set; }

    [Column("scenario_id")]
    public Guid? ScenarioId { get; set; }

    [Column("status")]
    public string Status { get; set; } = "active";

    [Column("started_at")]
    public DateTime? StartedAt { get; set; }

    [Column("ended_at")]
    public DateTime? EndedAt { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("expected_end")]
    public DateTime? ExpectedEnd { get; set; }

    [Column("last_status_id")]
    public Guid? LastStatusId { get; set; }

    [Column("last_status_updated_at")]
    public DateTime? LastStatusUpdatedAt { get; set; }
}
