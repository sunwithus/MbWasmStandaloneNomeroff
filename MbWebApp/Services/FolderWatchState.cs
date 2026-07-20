using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace MbWebApp.Services;

public enum FolderAfterAction
{
    Move = 0,
    Delete = 1
}

public sealed class FolderWatchConfig
{
    public bool Enabled { get; set; }
    public string WatchFolder { get; set; } = @"D:\REG_VIDEO";
    public FolderAfterAction AfterAction { get; set; } = FolderAfterAction.Move;
    public string MoveSubfolder { get; set; } = "Processed";
    public int IntervalSec { get; set; } = 2;
    public bool SaveToDb { get; set; } = true;
    public int DedupIntervalSec { get; set; } = 300;
    public bool SkipSaveWithoutGps { get; set; }
    public string DeviceName { get; set; } = "";
    public List<string> Watchlist { get; set; } = new();
    public int PollSeconds { get; set; } = 5;
    public int StableSeconds { get; set; } = 2;
    /// <summary>Подпапка watch-folder (или абсолютный путь) для disk-queue при сбоях OCR/IB.</summary>
    public string DiskQueueSubfolder { get; set; } = "_disk_queue";
}

public sealed class FolderWatchLogEntry
{
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
}

public sealed class FolderWatchStatusDto
{
    public bool Running { get; set; }
    public bool Processing { get; set; }
    public string? CurrentFile { get; set; }
    public string Message { get; set; } = "";
    public int Percent { get; set; }
    public int ProgressCurrent { get; set; }
    public int ProgressTotal { get; set; }
    public int QueueCount { get; set; }
    public List<string> Queue { get; set; } = new();
    public FolderWatchConfig Config { get; set; } = new();
    public List<FolderWatchLogEntry> Log { get; set; } = new();
    public string? LastResultSummary { get; set; }
    public int DiskQueueCount { get; set; }
}

/// <summary>Потокобезопасное состояние папки + config (appsettings/env → folder-watch.json).</summary>
public sealed class FolderWatchState
{
    private readonly object _lock = new();
    private readonly string _configPath;
    private readonly ILogger<FolderWatchState> _logger;
    private readonly IConfiguration _configuration;
    private FolderWatchConfig _config = new();
    private readonly Queue<string> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FolderWatchLogEntry> _log = new();
    private const int MaxLog = 80;

    public FolderWatchState(ILogger<FolderWatchState> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _configPath = Path.Combine(AppContext.BaseDirectory, "folder-watch.json");
        LoadFromDiskOrAppsettings();
    }

    public event Action? Changed;

    public bool Running { get; private set; }
    public bool Processing { get; private set; }
    public string? CurrentFile { get; private set; }
    public string Message { get; private set; } = "Остановлено";
    public int Percent { get; private set; }
    public int ProgressCurrent { get; private set; }
    public int ProgressTotal { get; private set; }
    public string? LastResultSummary { get; private set; }

    public FolderWatchConfig GetConfig()
    {
        lock (_lock)
            return Clone(_config);
    }

    public void SaveConfig(FolderWatchConfig cfg)
    {
        lock (_lock)
        {
            _config = Clone(cfg);
            _config.IntervalSec = Math.Clamp(_config.IntervalSec, 1, 60);
            _config.PollSeconds = Math.Clamp(_config.PollSeconds, 2, 120);
            _config.StableSeconds = Math.Clamp(_config.StableSeconds, 1, 30);
            if (string.IsNullOrWhiteSpace(_config.MoveSubfolder))
                _config.MoveSubfolder = "Processed";
            if (string.IsNullOrWhiteSpace(_config.DiskQueueSubfolder))
                _config.DiskQueueSubfolder = "_disk_queue";
            PersistLocked();
        }
        Notify();
    }

    public void SetRunning(bool running)
    {
        lock (_lock)
        {
            Running = running;
            Message = running ? "Мониторинг включён" : "Остановлено";
            if (!running && !Processing)
            {
                CurrentFile = null;
                Percent = 0;
            }
        }
        Notify();
    }

    public void Enqueue(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_lock)
        {
            if (_queued.Contains(full)) return;
            if (!IsVideoFile(full)) return;
            if (IsInProcessedFolder(full, _config)) return;
            // disk-queue files подхватываются отдельно
            var dq = Path.GetFullPath(Path.Combine(_config.WatchFolder,
                string.IsNullOrWhiteSpace(_config.DiskQueueSubfolder) ? "_disk_queue" : _config.DiskQueueSubfolder));
            if (full.StartsWith(dq + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;
            _queued.Add(full);
            _queue.Enqueue(full);
            AddLogLocked("info", $"В очередь: {Path.GetFileName(full)}");
        }
        Notify();
    }

    public bool TryDequeue(out string path)
    {
        lock (_lock)
        {
            while (_queue.Count > 0)
            {
                path = _queue.Dequeue();
                _queued.Remove(path);
                if (File.Exists(path) && !IsInProcessedFolder(path, _config))
                    return true;
            }
            path = "";
            return false;
        }
    }

    public void BeginProcessing(string path)
    {
        lock (_lock)
        {
            Processing = true;
            CurrentFile = path;
            Message = $"Обработка: {Path.GetFileName(path)}";
            Percent = 0;
            ProgressCurrent = 0;
            ProgressTotal = 0;
            AddLogLocked("info", $"Старт: {Path.GetFileName(path)}");
        }
        Notify();
    }

    public void ReportProgress(string message, int percent, int current, int total)
    {
        lock (_lock)
        {
            Message = message;
            if (percent > 0) Percent = Math.Clamp(percent, 0, 100);
            ProgressCurrent = current;
            ProgressTotal = total;
        }
        Notify();
    }

    public void EndProcessingSuccess(string path, string summary)
    {
        lock (_lock)
        {
            Processing = false;
            LastResultSummary = summary;
            AddLogLocked("ok", $"{Path.GetFileName(path)}: {summary}");
            CurrentFile = null;
            Percent = 100;
            Message = Running ? "Ожидание файлов..." : "Остановлено";
        }
        Notify();
    }

    public void EndProcessingError(string path, string error)
    {
        lock (_lock)
        {
            Processing = false;
            AddLogLocked("error", $"{Path.GetFileName(path)}: {error}");
            CurrentFile = null;
            Percent = 0;
            Message = Running ? "Ошибка, ждём следующий файл..." : "Остановлено";
        }
        Notify();
    }

    public FolderWatchStatusDto Snapshot()
    {
        lock (_lock)
        {
            return new FolderWatchStatusDto
            {
                Running = Running,
                Processing = Processing,
                CurrentFile = CurrentFile,
                Message = Message,
                Percent = Percent,
                ProgressCurrent = ProgressCurrent,
                ProgressTotal = ProgressTotal,
                QueueCount = _queue.Count,
                Queue = _queue.Select(Path.GetFileName).Where(n => n != null).Cast<string>().Take(20).ToList(),
                Config = Clone(_config),
                Log = _log.ToList(),
                LastResultSummary = LastResultSummary
            };
        }
    }

    public static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".avi" or ".mov" or ".mkv" or ".mts" or ".m4v" or ".wmv";
    }

    public static bool IsInProcessedFolder(string path, FolderWatchConfig cfg)
    {
        var folder = Path.GetFullPath(cfg.WatchFolder.TrimEnd('\\', '/'));
        var processed = Path.GetFullPath(Path.Combine(folder, cfg.MoveSubfolder));
        var full = Path.GetFullPath(path);
        return full.StartsWith(processed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(Path.GetDirectoryName(full), processed, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadFromDiskOrAppsettings()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var cfg = JsonSerializer.Deserialize<FolderWatchConfig>(json, JsonReadOpts);
                if (cfg != null)
                {
                    _config = cfg;
                    EnsureDiskQueueDefaults(_config);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать {Path}", _configPath);
        }

        _config = BindFromAppsettings();
        PersistLocked();
        _logger.LogInformation("FolderWatch: конфиг из appsettings/env → {Path}", _configPath);
    }

    private FolderWatchConfig BindFromAppsettings()
    {
        var section = _configuration.GetSection("FolderWatch");
        var cfg = new FolderWatchConfig();
        section.Bind(cfg);
        if (string.IsNullOrWhiteSpace(cfg.WatchFolder))
            cfg.WatchFolder = @"D:\REG_VIDEO";
        EnsureDiskQueueDefaults(cfg);
        return cfg;
    }

    private static void EnsureDiskQueueDefaults(FolderWatchConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.DiskQueueSubfolder))
            cfg.DiskQueueSubfolder = "_disk_queue";
        if (string.IsNullOrWhiteSpace(cfg.MoveSubfolder))
            cfg.MoveSubfolder = "Processed";
    }

    private void PersistLocked()
    {
        try
        {
            EnsureDiskQueueDefaults(_config);
            var json = JsonSerializer.Serialize(_config, JsonWriteOpts);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось сохранить {Path}", _configPath);
        }
    }

    private void AddLogLocked(string level, string message)
    {
        _log.Insert(0, new FolderWatchLogEntry { Level = level, Message = message, Utc = DateTime.UtcNow });
        while (_log.Count > MaxLog) _log.RemoveAt(_log.Count - 1);
    }

    private void Notify()
    {
        try { Changed?.Invoke(); } catch { /* ignore */ }
    }

    private static FolderWatchConfig Clone(FolderWatchConfig c) => new()
    {
        Enabled = c.Enabled,
        WatchFolder = c.WatchFolder,
        AfterAction = c.AfterAction,
        MoveSubfolder = c.MoveSubfolder,
        IntervalSec = c.IntervalSec,
        SaveToDb = c.SaveToDb,
        DedupIntervalSec = c.DedupIntervalSec,
        SkipSaveWithoutGps = c.SkipSaveWithoutGps,
        DeviceName = c.DeviceName ?? "",
        Watchlist = c.Watchlist?.ToList() ?? new List<string>(),
        PollSeconds = c.PollSeconds,
        StableSeconds = c.StableSeconds,
        DiskQueueSubfolder = string.IsNullOrWhiteSpace(c.DiskQueueSubfolder) ? "_disk_queue" : c.DiskQueueSubfolder
    };

    private static readonly JsonSerializerOptions JsonReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
