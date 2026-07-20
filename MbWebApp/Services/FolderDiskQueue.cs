using System.Text.Json;
using System.Text.Json.Serialization;

namespace MbWebApp.Services;

public sealed class FolderDiskQueueJob
{
    public string Id { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string StoredPath { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime EnqueuedUtc { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Disk-queue для folder watch: если OCR/IB недоступны или обработка упала —
/// видео паркуется на диск и позже ретраится (не теряется).
/// </summary>
public sealed class FolderDiskQueue
{
    private readonly ILogger<FolderDiskQueue> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FolderDiskQueue(ILogger<FolderDiskQueue> logger)
    {
        _logger = logger;
    }

    public string ResolveRoot(FolderWatchConfig cfg)
    {
        var sub = string.IsNullOrWhiteSpace(cfg.DiskQueueSubfolder) ? "_disk_queue" : cfg.DiskQueueSubfolder.Trim();
        if (Path.IsPathRooted(sub))
            return Path.GetFullPath(sub);
        return Path.GetFullPath(Path.Combine(cfg.WatchFolder, sub));
    }

    private static string JobsDir(string root) => Path.Combine(root, "jobs");
    private static string FilesDir(string root) => Path.Combine(root, "files");

    public void EnsureLayout(FolderWatchConfig cfg)
    {
        var root = ResolveRoot(cfg);
        Directory.CreateDirectory(JobsDir(root));
        Directory.CreateDirectory(FilesDir(root));
    }

    public bool IsUnderQueue(string path, FolderWatchConfig cfg)
    {
        try
        {
            var root = ResolveRoot(cfg);
            var full = Path.GetFullPath(path);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Переносит видео в disk-queue и пишет job JSON. Возвращает job id или null.</summary>
    public string? Park(string videoPath, FolderWatchConfig cfg, string reason)
    {
        if (!File.Exists(videoPath))
            return null;

        EnsureLayout(cfg);
        var root = ResolveRoot(cfg);
        var id = Guid.NewGuid().ToString("N");
        var ext = Path.GetExtension(videoPath);
        var stored = Path.Combine(FilesDir(root), id + ext);
        var jobPath = Path.Combine(JobsDir(root), id + ".json");

        try
        {
            File.Move(videoPath, stored, overwrite: false);
        }
        catch (IOException)
        {
            File.Copy(videoPath, stored, overwrite: true);
            try { File.Delete(videoPath); } catch { /* ignore */ }
        }

        var job = new FolderDiskQueueJob
        {
            Id = id,
            OriginalPath = videoPath,
            StoredPath = stored,
            Reason = reason,
            EnqueuedUtc = DateTime.UtcNow,
            Attempts = 0
        };
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job, JsonOpts));
        _logger.LogWarning("Disk-queue: parked {File} → {Id} ({Reason})", Path.GetFileName(videoPath), id, reason);
        return id;
    }

    public IReadOnlyList<FolderDiskQueueJob> ListReady(FolderWatchConfig cfg)
    {
        EnsureLayout(cfg);
        var root = ResolveRoot(cfg);
        var jobs = new List<FolderDiskQueueJob>();
        foreach (var file in Directory.EnumerateFiles(JobsDir(root), "*.json"))
        {
            try
            {
                var job = JsonSerializer.Deserialize<FolderDiskQueueJob>(File.ReadAllText(file), JsonOpts);
                if (job == null || string.IsNullOrWhiteSpace(job.StoredPath)) continue;
                if (!File.Exists(job.StoredPath)) continue;
                jobs.Add(job);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disk-queue: bad job {File}", file);
            }
        }
        return jobs.OrderBy(j => j.EnqueuedUtc).ToList();
    }

    public void MarkAttempt(FolderWatchConfig cfg, FolderDiskQueueJob job, string? error)
    {
        var root = ResolveRoot(cfg);
        var jobPath = Path.Combine(JobsDir(root), job.Id + ".json");
        job.Attempts++;
        job.LastAttemptUtc = DateTime.UtcNow;
        job.LastError = error;
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job, JsonOpts));
    }

    public void Complete(FolderWatchConfig cfg, FolderDiskQueueJob job)
    {
        var root = ResolveRoot(cfg);
        var jobPath = Path.Combine(JobsDir(root), job.Id + ".json");
        try { if (File.Exists(jobPath)) File.Delete(jobPath); } catch { /* ignore */ }
        // файл уже уйдёт через AfterAction
        _logger.LogInformation("Disk-queue: completed job {Id}", job.Id);
    }

    public int Count(FolderWatchConfig cfg)
    {
        try
        {
            EnsureLayout(cfg);
            return Directory.GetFiles(JobsDir(ResolveRoot(cfg)), "*.json").Length;
        }
        catch
        {
            return 0;
        }
    }
}
