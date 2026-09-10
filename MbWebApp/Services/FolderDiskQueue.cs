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
        // AfterAction должен был унести видео. Если оно ещё в queue и уже есть
        // копия в Processed — удаляем хвост. Если это единственная копия —
        // job оставляем, чтобы Sweep не счёл файл сиротой.
        TryDeleteStoredIfMoved(cfg, job);
        if (File.Exists(job.StoredPath))
        {
            _logger.LogWarning(
                "Disk-queue: {Id} завершён, но файл остался — job не удаляю, чтобы не потерять ролик",
                job.Id);
            return;
        }
        try { if (File.Exists(jobPath)) File.Delete(jobPath); } catch { /* ignore */ }
        _logger.LogInformation("Disk-queue: completed job {Id}", job.Id);
    }

    /// <summary>
    /// Убрать мёртвые job-файлы (видео уже нет) и сиротские ролики без job.
    /// Не удаляет файл, который сейчас обрабатывается.
    /// </summary>
    public void Sweep(FolderWatchConfig cfg, string? keepStoredPath = null)
    {
        try
        {
            EnsureLayout(cfg);
            var root = ResolveRoot(cfg);
            var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(JobsDir(root), "*.json"))
            {
                FolderDiskQueueJob? job = null;
                try
                {
                    job = JsonSerializer.Deserialize<FolderDiskQueueJob>(File.ReadAllText(file), JsonOpts);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Disk-queue: битый job {File} — удаляю", file);
                    try { File.Delete(file); } catch { /* ignore */ }
                    continue;
                }

                if (job == null || string.IsNullOrWhiteSpace(job.StoredPath) || !File.Exists(job.StoredPath))
                {
                    try { File.Delete(file); } catch { /* ignore */ }
                    continue;
                }
                knownFiles.Add(Path.GetFullPath(job.StoredPath));
            }

            foreach (var leftover in Directory.EnumerateFiles(FilesDir(root)))
            {
                var full = Path.GetFullPath(leftover);
                if (keepStoredPath != null
                    && string.Equals(full, Path.GetFullPath(keepStoredPath), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (knownFiles.Contains(full))
                    continue;
                // Job уже нет — возвращаем ролик в папку мониторинга, не удаляем.
                try
                {
                    var destDir = Path.GetFullPath(cfg.WatchFolder);
                    var dest = Path.Combine(destDir, Path.GetFileName(full));
                    if (File.Exists(dest))
                    {
                        var name = Path.GetFileNameWithoutExtension(full);
                        var ext = Path.GetExtension(full);
                        dest = Path.Combine(destDir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                    }
                    File.Move(full, dest);
                    _logger.LogInformation("Disk-queue: сирота {File} → {Dest}", Path.GetFileName(full), dest);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Disk-queue: не удалось вернуть {File}", full);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disk-queue sweep failed");
        }
    }

    private void TryDeleteStoredIfMoved(FolderWatchConfig cfg, FolderDiskQueueJob job)
    {
        if (string.IsNullOrWhiteSpace(job.StoredPath) || !File.Exists(job.StoredPath))
            return;
        try
        {
            var originalName = Path.GetFileName(
                string.IsNullOrWhiteSpace(job.OriginalPath) ? job.StoredPath : job.OriginalPath);
            var dest = Path.Combine(Path.GetFullPath(cfg.WatchFolder), cfg.MoveSubfolder, originalName);
            if (File.Exists(dest))
                File.Delete(job.StoredPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disk-queue: leftover {Path}", job.StoredPath);
        }
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
