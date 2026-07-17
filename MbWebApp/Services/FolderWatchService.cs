using System.Text.Json;
using MbWebApp.Models;
using Nomeroff.Video.Api;

namespace MbWebApp.Services;

/// <summary>Мониторинг папки: очередь видео → OCR → Move/Delete.</summary>
public sealed class FolderWatchService : BackgroundService
{
    private readonly FolderWatchState _state;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VideoFileProcessor _processor;
    private readonly ILogger<FolderWatchService> _logger;
    private readonly object _scanLock = new();
    private FileSystemWatcher? _watcher;
    private string? _watchingPath;
    private readonly ManualResetEventSlim _wake = new(false);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private int _drainAndStop;

    public FolderWatchService(
        FolderWatchState state,
        IServiceScopeFactory scopeFactory,
        VideoFileProcessor processor,
        ILogger<FolderWatchService> logger)
    {
        _state = state;
        _scopeFactory = scopeFactory;
        _processor = processor;
        _logger = logger;
    }

    public void RequestScan()
    {
        ScanFolder(_state.GetConfig());
        _wake.Set();
    }

    /// <summary>Разовый прогон: сканировать, обработать очередь, остановиться.</summary>
    public void RequestScanOnce()
    {
        Interlocked.Exchange(ref _drainAndStop, 1);
        ScanFolder(_state.GetConfig());
        _state.SetRunning(true);
        _wake.Set();
    }

    public void RequestStart()
    {
        Interlocked.Exchange(ref _drainAndStop, 0);
        var cfg = _state.GetConfig();
        cfg.Enabled = true;
        _state.SaveConfig(cfg);
        _state.SetRunning(true);
        EnsureWatcher(cfg);
        ScanFolder(cfg);
        _wake.Set();
    }

    public void RequestStop()
    {
        var cfg = _state.GetConfig();
        cfg.Enabled = false;
        _state.SaveConfig(cfg);
        _state.SetRunning(false);
        DisposeWatcher();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = _state.GetConfig();
        if (cfg.Enabled)
        {
            _state.SetRunning(true);
            EnsureWatcher(cfg);
            ScanFolder(cfg);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                cfg = _state.GetConfig();
                if (_state.Running)
                {
                    EnsureWatcher(cfg);
                    ScanFolder(cfg);

                    if (!_state.Processing && _state.TryDequeue(out var path))
                    {
                        await ProcessOneAsync(path, cfg, stoppingToken);
                        continue;
                    }

                    if (!_state.Processing
                        && Interlocked.CompareExchange(ref _drainAndStop, 0, 1) == 1
                        && _state.Snapshot().QueueCount == 0)
                    {
                        // разовый скан завершён
                        var stopCfg = _state.GetConfig();
                        stopCfg.Enabled = false;
                        _state.SaveConfig(stopCfg);
                        _state.SetRunning(false);
                        DisposeWatcher();
                    }
                }
                else
                {
                    DisposeWatcher();
                }

                _wake.Wait(TimeSpan.FromSeconds(Math.Max(2, cfg.PollSeconds)), stoppingToken);
                _wake.Reset();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FolderWatch loop error");
                await Task.Delay(3000, stoppingToken);
            }
        }

        DisposeWatcher();
    }

    private async Task ProcessOneAsync(string path, FolderWatchConfig cfg, CancellationToken ct)
    {
        if (!await WaitUntilStableAsync(path, cfg.StableSeconds, ct))
        {
            _state.EndProcessingError(path, "Файл не стабилизировался (ещё пишется?)");
            return;
        }

        _state.BeginProcessing(path);
        ProcessVideoResponse? apiResponse = null;
        string? error = null;

        try
        {
            await _processor.ProcessAsync(path, cfg.IntervalSec, async (payload, token) =>
            {
                var json = JsonSerializer.Serialize(payload);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type == "progress")
                {
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    var pct = root.TryGetProperty("percent", out var p) ? p.GetInt32() : 0;
                    var cur = root.TryGetProperty("current", out var c) ? c.GetInt32() : 0;
                    var tot = root.TryGetProperty("total", out var totEl) ? totEl.GetInt32() : 0;
                    _state.ReportProgress(msg, pct, cur, tot);
                }
                else if (type == "error")
                {
                    error = root.TryGetProperty("message", out var em) ? em.GetString() : "Ошибка обработки";
                }
                else if (type == "result")
                {
                    apiResponse = JsonSerializer.Deserialize<ProcessVideoResponse>(json, JsonOpts);
                }

                await Task.CompletedTask;
            }, ct);

            if (error != null)
            {
                _state.EndProcessingError(path, error);
                return;
            }
            if (apiResponse == null)
            {
                _state.EndProcessingError(path, "Пустой ответ OCR");
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<VideoResultProcessor>();
            var outcome = await processor.ProcessAsync(new VideoResultProcessRequest
            {
                ApiResponse = apiResponse,
                SaveToDb = cfg.SaveToDb,
                DedupIntervalSec = cfg.DedupIntervalSec,
                SkipSaveWithoutGps = cfg.SkipSaveWithoutGps,
                Watchlist = cfg.Watchlist,
                DeviceName = cfg.DeviceName,
                Source = "folder_watch",
                Progress = new Progress<(int current, int total, string message)>(p =>
                    _state.ReportProgress(p.message, (int)(100.0 * p.current / Math.Max(p.total, 1)), p.current, p.total))
            }, ct);

            ApplyAfterAction(path, cfg);

            var summary =
                $"кадров {outcome.Summary.TotalFrames}, номеров {outcome.Hits.Count}, уникальных {outcome.Summary.UniquePlates}" +
                (cfg.SaveToDb ? $", в БД с GPS {outcome.Summary.SavedWithGps}" : "");
            _state.EndProcessingSuccess(path, summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FolderWatch process failed: {Path}", path);
            _state.EndProcessingError(path, ex.Message);
        }
    }

    private void ApplyAfterAction(string path, FolderWatchConfig cfg)
    {
        try
        {
            if (cfg.AfterAction == FolderAfterAction.Delete)
            {
                File.Delete(path);
                _logger.LogInformation("Deleted {Path}", path);
                return;
            }

            var folder = Path.GetFullPath(cfg.WatchFolder);
            var destDir = Path.Combine(folder, cfg.MoveSubfolder);
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, Path.GetFileName(path));
            if (File.Exists(dest))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var ext = Path.GetExtension(path);
                dest = Path.Combine(destDir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
            }
            File.Move(path, dest);
            _logger.LogInformation("Moved {From} -> {To}", path, dest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AfterAction failed for {Path}", path);
        }
    }

    private void ScanFolder(FolderWatchConfig cfg)
    {
        lock (_scanLock)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cfg.WatchFolder) || !Directory.Exists(cfg.WatchFolder))
                    return;

                foreach (var file in Directory.EnumerateFiles(cfg.WatchFolder))
                {
                    if (!FolderWatchState.IsVideoFile(file)) continue;
                    if (FolderWatchState.IsInProcessedFolder(file, cfg)) continue;
                    _state.Enqueue(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scan failed for {Folder}", cfg.WatchFolder);
            }
        }
    }

    private void EnsureWatcher(FolderWatchConfig cfg)
    {
        var path = Path.GetFullPath(cfg.WatchFolder);
        if (!Directory.Exists(path))
        {
            DisposeWatcher();
            return;
        }

        if (_watcher != null && string.Equals(_watchingPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        DisposeWatcher();
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnFsEvent;
            _watcher.Changed += OnFsEvent;
            _watcher.Renamed += (_, e) => OnPath(e.FullPath);
            _watchingPath = path;
            _logger.LogInformation("Watching folder {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileSystemWatcher failed for {Path}", path);
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => OnPath(e.FullPath);

    private void OnPath(string path)
    {
        if (!FolderWatchState.IsVideoFile(path)) return;
        _state.Enqueue(path);
        _wake.Set();
    }

    private void DisposeWatcher()
    {
        if (_watcher == null) return;
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch { /* ignore */ }
        _watcher = null;
        _watchingPath = null;
    }

    private static async Task<bool> WaitUntilStableAsync(string path, int stableSeconds, CancellationToken ct)
    {
        long lastSize = -1;
        var stableFor = TimeSpan.Zero;
        var need = TimeSpan.FromSeconds(Math.Max(1, stableSeconds));
        var deadline = DateTime.UtcNow.AddMinutes(10);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return false;
            long size;
            try
            {
                var info = new FileInfo(path);
                size = info.Length;
                // try open exclusively
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException)
            {
                lastSize = -1;
                stableFor = TimeSpan.Zero;
                await Task.Delay(500, ct);
                continue;
            }

            if (size == lastSize && size > 0)
            {
                stableFor += TimeSpan.FromMilliseconds(500);
                if (stableFor >= need) return true;
            }
            else
            {
                lastSize = size;
                stableFor = TimeSpan.Zero;
            }
            await Task.Delay(500, ct);
        }
        return false;
    }

    public override void Dispose()
    {
        DisposeWatcher();
        _wake.Dispose();
        base.Dispose();
    }
}
