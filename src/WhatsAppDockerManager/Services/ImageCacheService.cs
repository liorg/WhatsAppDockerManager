using System.Collections.Concurrent;
using System.Diagnostics;

namespace WhatsAppDockerManager.Services;

public record ImagePullResult(string Image, bool Ok, bool Updated, string? Id, long Ms, string? Error);

public interface IImageCacheService
{
    /// <summary>מבטיח שה-image נמצא ב-cache. pull חוסם רק אם אין image מקומי.</summary>
    Task<bool> EnsureCachedAsync(string image, CancellationToken ct = default);

    /// <summary>מושך את כל ה-images של providers פעילים (מעדכן את ה-cache).</summary>
    Task<List<ImagePullResult>> PrepullAllAsync(CancellationToken ct = default);

    Task<DockerImageInfo?> GetInfoAsync(string image);
}

/// <summary>
/// שומר את ה-images של כל ה-providers עדכניים ב-cache המקומי:
///   • בעלייה — pull ברקע (לא חוסם את הקמת ה-containers)
///   • כל ImagePrepullMinutes (ברירת מחדל 10)
///   • לפי דרישה — POST /api/images/prepull (update.sh)
/// </summary>
public class ImageCacheService : BackgroundService, IImageCacheService
{
    private readonly IDockerService   _docker;
    private readonly ISupabaseService _supabase;
    private readonly ILogger<ImageCacheService> _logger;
    private readonly TimeSpan _interval;

    // pull אחד בכל פעם לכל image
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public ImageCacheService(
        IDockerService docker,
        ISupabaseService supabase,
        IConfiguration configuration,
        ILogger<ImageCacheService> logger)
    {
        _docker   = docker;
        _supabase = supabase;
        _logger   = logger;
        _interval = TimeSpan.FromMinutes(Math.Max(1,
            configuration.GetValue<int?>("AppSettings:Docker:ImagePrepullMinutes") ?? 10));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // השהיה קצרה — לתת ל-InitializeAsync להקים containers מה-cache קודם
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PrepullAllAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "[IMAGES] Prepull cycle failed"); }

            try { await Task.Delay(_interval, stoppingToken); } catch { return; }
        }
    }

    public Task<DockerImageInfo?> GetInfoAsync(string image) => _docker.GetImageInfoAsync(image);

    public async Task<bool> EnsureCachedAsync(string image, CancellationToken ct = default)
    {
        if (await _docker.GetImageInfoAsync(image) != null) return true;

        _logger.LogInformation("[IMAGES] {Image} not in cache — pulling (blocking)", image);
        var r = await PullAsync(image, ct);
        return r.Ok;
    }

    public async Task<List<ImagePullResult>> PrepullAllAsync(CancellationToken ct = default)
    {
        var images = await _supabase.GetProviderImagesAsync();
        _logger.LogInformation("[IMAGES] Prepull {Count} images: {Images}", images.Count, string.Join(", ", images));

        var results = await Task.WhenAll(images.Select(i => PullAsync(i, ct)));

        foreach (var r in results)
        {
            if (!r.Ok)
                _logger.LogWarning("[IMAGES] ❌ {Image} pull failed: {Error}", r.Image, r.Error);
            else if (r.Updated)
                _logger.LogInformation("[IMAGES] ⬆️ {Image} updated → {Id} ({Ms}ms)", r.Image, Short(r.Id), r.Ms);
            else
                _logger.LogInformation("[IMAGES] ✅ {Image} up to date {Id} ({Ms}ms)", r.Image, Short(r.Id), r.Ms);
        }
        return results.ToList();
    }

    private async Task<ImagePullResult> PullAsync(string image, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(image, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            var before = (await _docker.GetImageInfoAsync(image))?.Id;
            var ok     = await _docker.PullImageAsync(image);
            var after  = (await _docker.GetImageInfoAsync(image))?.Id;

            return new ImagePullResult(image, ok && after != null, before != after && after != null,
                after, sw.ElapsedMilliseconds, ok ? null : "pull returned false");
        }
        catch (Exception ex)
        {
            return new ImagePullResult(image, false, false, null, sw.ElapsedMilliseconds, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? Short(string? id) => id?[..Math.Min(19, id.Length)];
}
