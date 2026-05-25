using Microsoft.AspNetCore.Mvc;
using WhatsAppDockerManager.Models;
using WhatsAppDockerManager.Services;

namespace WhatsAppDockerManager.Controllers;

[ApiController]
[Route("api/media")]
public class MediaController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MediaController> _logger;
    private readonly string _mediaBasePath;

    public MediaController(IConfiguration configuration, ILogger<MediaController> logger)
    {
        _configuration = configuration;
        _logger        = logger;
        _mediaBasePath = configuration["AppSettings:Docker:DataBasePath"] 
                      ?? "/data";
    }

    // GET /api/media/{phoneId}/{messageId}
    [HttpGet("{phoneId}/{messageId}")]
    public IActionResult GetMedia(string phoneId, string messageId)
    {
        // ── מצא את הקובץ — לא יודעים את הextension ─────────────
        var dir = Path.Combine(_mediaBasePath, phoneId, "media");
        if (!Directory.Exists(dir))
            return NotFound(new { error = "Phone media dir not found" });

        // חפש לפי messageId (כל extension)
        var file = Directory.GetFiles(dir, $"{messageId}.*").FirstOrDefault();
        if (file == null)
            return NotFound(new { error = "Media not found" });

        var ext      = Path.GetExtension(file).TrimStart('.').ToLower();
        var mimeType = ext switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png"           => "image/png",
            "webp"          => "image/webp",
            "ogg"           => "audio/ogg",
            "mp3"           => "audio/mpeg",
            "mp4"           => "video/mp4",
            _               => "application/octet-stream"
        };

        _logger.LogInformation("[MEDIA] Serving {File}", file);
        return PhysicalFile(file, mimeType);
    }
}