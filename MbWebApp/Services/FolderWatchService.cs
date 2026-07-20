using System.Text.Json;
using MbWebApp.Models;
using Nomeroff.Video.Api;

namespace MbWebApp.Services;

/// <summary>Мониторинг папки: очередь видео → OCR → Move/Delete; disk-queue при сбоях OCR/IB.</summary>
public sealed class FolderWatchService : BackgroundService
{
    private readonly FolderWatchState _state;
    private readonly FolderDiskQueue _diskQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VideoFileProcessor _processor;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
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
        FolderDiskQueue diskQueue,
        IServiceScopeFactory scopeFactory,
        VideoFileProcessor processor,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<FolderWatchService> logger)
    {
        _state = state;
        _diskQueue = diskQueue;
        _scopeFactory = scopeFactory;
        _processor = processor;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public void RequestScan()
    {
        ScanFolder(_state.GetConfig());
        _wake.Set();
    }

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

                    if (!_state.Processing)
                    {
                        // Сначала disk-queue (отложенные из-за OCR/IB)
                        if (await TryProcessDiskQueueAsync(cfg, stoppingToken))
                            continue;

                        if (_state.TryDequeue(out var path))
                        {
                            await ProcessOneAsync(path, cfg, stoppingToken, fromDiskQueue: null);
                            continue;
                        }
                    }

                    if (!_state.Processing
                        && Interlocked.CompareExchange(ref _drainAndStop, 0, 1) == 1
                        && _state.Snapshot().QueueCount == 0
                        && _diskQueue.Count(cfg) == 0)
                    {
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

    private async Task<bool> TryProcessDiskQueueAsync(FolderWatchConfig cfg, CancellationToken ct)
    {
        var jobs = _diskQueue.ListReady(cfg);
        if (jobs.Count == 0) return false;

        if (!await IsOcrAvailableAsync(ct))
        {
            _logger.LogDebug("Disk-queue: OCR недоступен, ждём ({Count} jobs)", jobs.Count);
            return false;
        }

        if (cfg.SaveToDb && !await IsInterbaseAvailableAsync(ct))
        {
            _logger.LogDebug("Disk-queue: InterBase недоступен, ждём ({Count} jobs)", jobs.Count);
            return false;
        }

        var job = jobs[0];
        _diskQueue.MarkAttempt(cfg, job, null);
        await ProcessOneAsync(job.StoredPath, cfg, ct, fromDiskQueue: job);
        return true;
    }

    private async Task ProcessOneAsync(
        string path,
        FolderWatchConfig cfg,
        CancellationToken ct,
        FolderDiskQueueJob? fromDiskQueue)
    {
        if (!await WaitUntilStableAsync(path, cfg.StableSeconds, ct))
        {
            _state.EndProcessingError(path, "Файл не стабилизировался (ещё пишется?)");
            return;
        }

        // До OCR: если сервисы лежат — паркуем на диск, не крутим зря
        if (fromDiskQueue == null)
        {
            if (!await IsOcrAvailableAsync(ct))
            {
                _diskQueue.Park(path, cfg, "OCR недоступен");
                _state.EndProcessingError(path, "OCR недоступен → файл в disk-queue");
                return;
            }
            if (cfg.SaveToDb && !await IsInterbaseAvailableAsync(ct))
            {
                _diskQueue.Park(path, cfg, "InterBase недоступен");
                _state.EndProcessingError(path, "InterBase недоступен → файл в disk-queue");
                return;
            }
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
                await FailOrParkAsync(path, cfg, fromDiskQueue, error);
                return;
            }
            if (apiResponse == null)
            {
                await FailOrParkAsync(path, cfg, fromDiskQueue, "Пустой ответ OCR");
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

            if (cfg.SaveToDb && outcome.Summary.SaveAttempts > 0 && outcome.Summary.SaveFailures == outcome.Summary.SaveAttempts)
            {
                await FailOrParkAsync(path, cfg, fromDiskQueue, "InterBase: все сохранения не удались");
                return;
            }

            ApplyAfterAction(path, cfg, preferredName: fromDiskQueue?.OriginalPath);
            if (fromDiskQueue != null)
                _diskQueue.Complete(cfg, fromDiskQueue);

            var summary =
                $"кадров {outcome.Summary.TotalFrames}, номеров {outcome.Hits.Count}, уникальных {outcome.Summary.UniquePlates}" +
                (cfg.SaveToDb ? $", в БД с GPS {outcome.Summary.SavedWithGps}" : "");
            _state.EndProcessingSuccess(path, summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FolderWatch process failed: {Path}", path);
            await FailOrParkAsync(path, cfg, fromDiskQueue, ex.Message);
        }
    }

    private Task FailOrParkAsync(string path, FolderWatchConfig cfg, FolderDiskQueueJob? fromDiskQueue, string error)
    {
        if (fromDiskQueue != null)
        {
            _diskQueue.MarkAttempt(cfg, fromDiskQueue, error);
            _state.EndProcessingError(path, $"disk-queue retry later: {error}");
            return Task.CompletedTask;
        }

        // Не паркуем, если файл уже в queue (IsUnderQueue) — избегаем циклов
        if (!_diskQueue.IsUnderQueue(path, cfg))
        {
            try
            {
                _diskQueue.Park(path, cfg, error);
                _state.EndProcessingError(path, $"{error} → disk-queue");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disk-queue park failed for {Path}", path);
            }
        }

        _state.EndProcessingError(path, error);
        return Task.CompletedTask;
    }

    private async Task<bool> IsOcrAvailableAsync(CancellationToken ct)
    {
        try
        {
            var baseUrl = (_config["NomeroffApiBaseUrl"] ?? "http://127.0.0.1:8000").TrimEnd('/');
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            using var resp = await client.GetAsync($"{baseUrl}/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsInterbaseAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbManager = scope.ServiceProvider.GetRequiredService<Nomeroff.Interbase.Api.Interbase.DbManager>();
            var ib = scope.ServiceProvider.GetRequiredService<Nomeroff.Interbase.Api.Interbase.NomeroffInterbaseService>();
            var list = dbManager.ListDatabases();
            if (list.Count == 0 && !ib.IsConfigured)
                return false;
            var db = list.FirstOrDefault() ?? _config["Interbase:DefaultDb"];
            var connStr = !string.IsNullOrWhiteSpace(db)
                ? dbManager.GetConnectionString(db)
                : (_config["Interbase:ConnectionString"] ?? "");
            if (string.IsNullOrWhiteSpace(connStr))
                return ib.IsConfigured;
            var svc = ib.WithConnection(connStr);
            var (ok, _) = await svc.TestConnectionAsync(ct);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "InterBase availability check failed");
            return false;
        }
    }

    private void ApplyAfterAction(string path, FolderWatchConfig cfg, string? preferredName = null)
    {
        try
        {
            var fileName = Path.GetFileName(!string.IsNullOrWhiteSpace(preferredName) ? preferredName : path);
            if (cfg.AfterAction == FolderAfterAction.Delete)
            {
                File.Delete(path);
                _logger.LogInformation("Deleted {Path}", path);
                return;
            }

            var folder = Path.GetFullPath(cfg.WatchFolder);
            var destDir = Path.Combine(folder, cfg.MoveSubfolder);
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, fileName);
            if (File.Exists(dest))
            {
                var name = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
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

                _diskQueue.EnsureLayout(cfg);

                foreach (var file in Directory.EnumerateFiles(cfg.WatchFolder))
                {
                    if (!FolderWatchState.IsVideoFile(file)) continue;
                    if (FolderWatchState.IsInProcessedFolder(file, cfg)) continue;
                    if (_diskQueue.IsUnderQueue(file, cfg)) continue;
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
