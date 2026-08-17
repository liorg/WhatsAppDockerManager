using Docker.DotNet;
using Docker.DotNet.Models;

namespace WhatsAppDockerManager.Services;

/// <summary>משאבי מערכת של ה-host. null = לא הצלחנו לקרוא.</summary>
public record HostMetrics(
    double? CpuPercent,
    int?    RamTotalMb,
    int?    RamUsedMb,
    int?    DiskTotalGb,
    int?    DiskUsedGb,
    int?    ContainerCount
);

/// <summary>
/// קורא משאבי מערכת מ-/proc. static — שומר דגימת CPU קודמת בין קריאות.
/// </summary>
public static class SystemMetrics
{
    private static long _prevIdle;
    private static long _prevTotal;
    private static readonly object _lock = new();

    public static async Task<HostMetrics> CollectAsync(CancellationToken ct = default)
    {
        var (ramTotal, ramUsed)   = ReadMemory();
        var (diskTotal, diskUsed) = ReadDisk();

        return new HostMetrics(
            CpuPercent:     ReadCpuPercent(),
            RamTotalMb:     ramTotal,
            RamUsedMb:      ramUsed,
            DiskTotalGb:    diskTotal,
            DiskUsedGb:     diskUsed,
            ContainerCount: await CountContainersAsync(ct)
        );
    }

    // ── CPU: דלתא בין שתי דגימות של /proc/stat ─────────────────────────────
    // הקריאה הראשונה מחזירה null (אין דגימה קודמת להשוות אליה).
    private static double? ReadCpuPercent()
    {
        if (!ReadCpuSample(out var idle, out var total)) return null;

        lock (_lock)
        {
            var prevIdle  = _prevIdle;
            var prevTotal = _prevTotal;
            _prevIdle  = idle;
            _prevTotal = total;

            if (prevTotal == 0) return null;

            var idleDelta  = idle  - prevIdle;
            var totalDelta = total - prevTotal;
            if (totalDelta <= 0) return null;

            return Math.Round(
                Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100), 2);
        }
    }

    private static bool ReadCpuSample(out long idle, out long total)
    {
        idle = 0; total = 0;
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu "));
            if (line is null) return false;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                            .Select(p => long.TryParse(p, out var v) ? v : 0).ToArray();
            if (parts.Length < 5) return false;

            idle  = parts[3] + parts[4];   // idle + iowait
            total = parts.Sum();
            return true;
        }
        catch { return false; }
    }

    // ── RAM: /proc/meminfo ─────────────────────────────────────────────────
    private static (int? totalMb, int? usedMb) ReadMemory()
    {
        try
        {
            var kb = new Dictionary<string, long>();
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var i = line.IndexOf(':');
                if (i <= 0) continue;
                var val = line[(i + 1)..].Trim().Split(' ')[0];
                if (long.TryParse(val, out var v)) kb[line[..i]] = v;
            }

            var total = kb.GetValueOrDefault("MemTotal");
            if (total <= 0) return (null, null);

            var avail = kb.TryGetValue("MemAvailable", out var a)
                ? a
                : kb.GetValueOrDefault("MemFree") + kb.GetValueOrDefault("Cached");

            return ((int)(total / 1024), (int)((total - avail) / 1024));
        }
        catch { return (null, null); }
    }

    // ── Disk ───────────────────────────────────────────────────────────────
    private static (int? totalGb, int? usedGb) ReadDisk()
    {
        try
        {
            // אם דיסק הנתונים נפרד מ-/ — שנה כאן למאונט הרלוונטי
            var drive = new DriveInfo("/");
            if (!drive.IsReady || drive.TotalSize <= 0) return (null, null);

            const double GB = 1024d * 1024 * 1024;
            return ((int)Math.Round(drive.TotalSize / GB),
                    (int)Math.Round((drive.TotalSize - drive.AvailableFreeSpace) / GB));
        }
        catch { return (null, null); }
    }

    // ── ספירת קונטיינרים רצים על ה-host ────────────────────────────────────
    private static async Task<int?> CountContainersAsync(CancellationToken ct)
    {
        try
        {
            using var docker = new DockerClientConfiguration(
                new Uri("unix:///var/run/docker.sock")).CreateClient();
            var list = await docker.Containers.ListContainersAsync(
                new ContainersListParameters { All = false }, ct);

            return list.Count;
        }
        catch { return null; }
    }
}
