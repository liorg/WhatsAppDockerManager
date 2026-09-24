using System.Net;
using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Services;

namespace WhatsAppDockerManager.Controllers;

/// <summary>
/// ניהול cache של images — localhost בלבד.
///   POST /api/images/prepull   → מושך את כל ה-images של providers פעילים (update.sh)
///   GET  /api/images           → מצב ה-cache
/// </summary>
[ApiController]
[Route("api/images")]
public class ImagesController : ControllerBase
{
    private readonly IImageCacheService _images;
    private readonly ISupabaseService   _supabase;

    public ImagesController(IImageCacheService images, ISupabaseService supabase)
    {
        _images   = images;
        _supabase = supabase;
    }

    [HttpPost("prepull")]
    public async Task<IActionResult> Prepull(CancellationToken ct)
    {
        if (!IsLocal()) return StatusCode(403);

        var results = await _images.PrepullAllAsync(ct);
        var ok      = results.All(r => r.Ok);
        return StatusCode(ok ? 200 : 502, new { ok, results });
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!IsLocal()) return StatusCode(403);

        var images = await _supabase.GetProviderImagesAsync();
        var list   = new List<object>();
        foreach (var image in images)
        {
            var info = await _images.GetInfoAsync(image);
            list.Add(new { image, cached = info != null, id = info?.Id, created = info?.Created });
        }
        return Ok(list);
    }

    private bool IsLocal()
    {
        var ip = HttpContext.Connection.RemoteIpAddress;
        return ip != null && IPAddress.IsLoopback(ip);
    }
}