// Services/SupabaseService.CloudApi.cs
//
// שתי עריכות נדרשות בקבצים הקיימים, אחרת CS0260:
//   ISupabaseService.cs    public interface  ->  public partial interface
//   SupabaseService.cs     public class      ->  public partial class
//
// ה-RPC phone_cloudapi_config חייב להיות בדאטהבייס. ה-SQL בקובץ
// sql/phone_cloudapi_config.sql

using System.Text.Json;
using System.Text.Json.Serialization;
using WhatsAppDockerManager.Models;

namespace WhatsAppDockerManager.Services;

public partial interface ISupabaseService
{
    Task<CloudApiSpec?> GetCloudApiSpecAsync(Guid phoneId);

    Task SetPhoneCloudApiConfigAsync(Guid phoneId, string? wabaId, string? phoneNumberId,
                                     string? accessToken, string? verifyToken);
}

public partial class SupabaseService
{
    /// <summary>
    /// המזהים של Cloud API לטלפון בודד. נקרא רק בטלפוני cloudapi — הנתיב
    /// החם של baileys לא נוגע בזה.
    /// </summary>
    public async Task<CloudApiSpec?> GetCloudApiSpecAsync(Guid phoneId)
    {
        try
        {
            var res = await _client.Rpc("phone_cloudapi_config", new Dictionary<string, object>
            {
                ["p_phone_id"] = phoneId,
            });

            var content = res.Content;
            if (string.IsNullOrWhiteSpace(content) || content.Trim() == "null")
            {
                _logger.LogWarning("[RPC] phone_cloudapi_config returned nothing for {PhoneId}", phoneId);
                return null;
            }

            var row = JsonSerializer.Deserialize<CloudApiConfigRow>(content);
            if (row == null || string.IsNullOrWhiteSpace(row.PhoneNumberId))
                return null;

            _logger.LogInformation("[RPC] cloudapi config phone={PhoneId} waba={Waba} pnid={Pnid} ownToken={Own}",
                phoneId, row.WabaId, row.PhoneNumberId, !string.IsNullOrEmpty(row.CloudAccessToken));

            return new CloudApiSpec
            {
                WabaId        = row.WabaId ?? "",
                PhoneNumberId = row.PhoneNumberId!,
                AccessToken   = row.CloudAccessToken ?? "",
                VerifyToken   = row.CloudVerifyToken,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RPC] phone_cloudapi_config failed for {PhoneId}", phoneId);
            return null;
        }
    }

    /// <summary>
    /// כותב את פרטי מטא של הלקוח ל-phones. נקרא מ-Provision, שהיא נקודת
    /// הכניסה היחידה שבה הם נכנסים למערכת.
    ///
    /// null בשדה = לא לשנות. שרשור מותנה ולא ארבעה Set קבועים, כי השמה של
    /// מחרוזת ריקה הייתה מוחקת טוקן של לקוח ב-provision חוזר.
    /// </summary>
    public async Task SetPhoneCloudApiConfigAsync(Guid phoneId, string? wabaId, string? phoneNumberId,
                                                  string? accessToken, string? verifyToken)
    {
        if (wabaId == null && phoneNumberId == null && accessToken == null && verifyToken == null)
            return;

        try
        {
            var q = _client.From<Phone>().Where(x => x.Id == phoneId);

            if (wabaId        != null) q = q.Set(x => x.WabaId!,           wabaId);
            if (phoneNumberId != null) q = q.Set(x => x.PhoneNumberId!,    phoneNumberId);
            if (accessToken   != null) q = q.Set(x => x.CloudAccessToken!, accessToken);
            if (verifyToken   != null) q = q.Set(x => x.CloudVerifyToken!, verifyToken);

            await q.Update();

            _logger.LogInformation("[DB] cloudapi config saved phone={PhoneId} waba={Waba} pnid={Pnid} token={HasToken}",
                phoneId, wabaId, phoneNumberId, !string.IsNullOrEmpty(accessToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DB] SetPhoneCloudApiConfigAsync failed for {PhoneId}", phoneId);
            throw;
        }
    }

    private sealed class CloudApiConfigRow
    {
        [JsonPropertyName("waba_id")]            public string? WabaId           { get; set; }
        [JsonPropertyName("phone_number_id")]    public string? PhoneNumberId    { get; set; }
        [JsonPropertyName("cloud_access_token")] public string? CloudAccessToken { get; set; }
        [JsonPropertyName("cloud_verify_token")] public string? CloudVerifyToken { get; set; }
    }
}