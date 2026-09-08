using System.Net.Http.Json;
using System.Text.Json;
using MbWebApp.Models;
using Nomeroff.Interbase.Api.Interbase;
using Nomeroff.Video.Api;

namespace MbWebApp.Services;

public class RecordsService
{
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly VideoFileProcessor _videoProcessor;
    private readonly DbManager _dbManager;
    private readonly ILogger<RecordsService>? _logger;

    private static readonly JsonSerializerOptions VideoJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RecordsService(
        HttpClient http,
        SettingsService settings,
        VideoFileProcessor videoProcessor,
        DbManager dbManager,
        ILogger<RecordsService>? logger = null)
    {
        _http = http;
        _settings = settings;
        _videoProcessor = videoProcessor;
        _dbManager = dbManager;
        _logger = logger;
    }

    private async Task<HttpClient?> GetRecordsApiClientAsync()
    {
        var baseUrl = await _settings.GetRecordsApiBaseUrlAsync();
        if (string.IsNullOrEmpty(baseUrl)) return null;
        return new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
    }

    private async Task<HttpClient?> GetGpsApiClientAsync()
    {
        var baseUrl = await _settings.GetGpsApiBaseUrlAsync();
        if (string.IsNullOrEmpty(baseUrl)) return null;
        return new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
    }

    public async Task<string?> GetDeviceNameFromApiAsync(CancellationToken ct = default)
    {
        using var client = await GetRecordsApiClientAsync();
        if (client == null) return null;
        try
        {
            var r = await client.GetFromJsonAsync<JsonElement>("api/device-name", ct);
            return r.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        catch { return null; }
    }

    public async Task<IReadOnlyList<string>> GetGpsPortsAsync(CancellationToken ct = default)
    {
        using var client = await GetGpsApiClientAsync();
        if (client == null) return Array.Empty<string>();
        try
        {
            var arr = await client.GetFromJsonAsync<string[]>("api/ports", ct);
            return arr ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task<bool> ConnectGpsAsync(string port, CancellationToken ct = default)
    {
        using var client = await GetGpsApiClientAsync();
        if (client == null) return false;
        try
        {
            var r = await client.PostAsJsonAsync("api/connect", new { port }, ct);
            if (!r.IsSuccessStatusCode) return false;
            var json = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            return json.TryGetProperty("success", out var s) && s.GetBoolean();
        }
        catch { return false; }
    }

    public async Task<(double? lat, double? lon)> GetGpsPositionAsync(CancellationToken ct = default)
    {
        using var client = await GetGpsApiClientAsync();
        if (client == null) return (null, null);
        try
        {
            var r = await client.GetFromJsonAsync<JsonElement>("api/position", ct);
            if (r.ValueKind != JsonValueKind.Object) return (null, null);
            if (r.TryGetProperty("latitude", out var latProp) && latProp.ValueKind == JsonValueKind.Number
                && r.TryGetProperty("longitude", out var lonProp) && lonProp.ValueKind == JsonValueKind.Number)
                return (latProp.GetDouble(), lonProp.GetDouble());
        }
        catch { }
        return (null, null);
    }

    public async Task<(bool success, string message)> TestGpsAsync(CancellationToken ct = default)
    {
        using var client = await GetGpsApiClientAsync();
        if (client == null) return (false, "GPS API не настроен.");
        try
        {
            var r = await client.GetFromJsonAsync<JsonElement>("api/test", ct);
            var ok = r.TryGetProperty("success", out var s) && s.GetBoolean();
            var msg = r.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (ok && r.TryGetProperty("latitude", out var lat) && r.TryGetProperty("longitude", out var lon))
                msg = $"Широта: {lat.GetDouble()}, Долгота: {lon.GetDouble()}";
            return (ok, msg ?? (ok ? "OK" : "Ошибка"));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool success, string message)> TestDbAsync(string? db = null, CancellationToken ct = default)
    {
        using var client = await GetRecordsApiClientAsync();
        if (client == null)
        {
            _logger?.LogDebug("TestDbAsync: API не настроен");
            return (false, "API записей не настроен.");
        }
        var url = string.IsNullOrEmpty(db) ? "api/db/test" : $"api/db/test?db={Uri.EscapeDataString(db)}";
        try
        {
            _logger?.LogDebug("TestDbAsync: GET {Url}", url);
            var r = await client.GetFromJsonAsync<JsonElement>(url, ct);
            var ok = r.TryGetProperty("success", out var s) && s.GetBoolean();
            var msg = r.TryGetProperty("message", out var m) ? m.GetString() : null;
            _logger?.LogInformation("TestDbAsync: success={Success}, message={Message}", ok, msg);
            return (ok, msg ?? (ok ? "OK" : "Ошибка"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TestDbAsync failed for db={Db}", db);
            return (false, ex.Message);
        }
    }

    public async Task<string?> GetOrCreateCurrentDbAsync(CancellationToken ct = default)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var dbFileName = today + ".IBS";
        var (storedDb, storedDate) = await _settings.GetCurrentDbAsync();
        if (storedDate == today && !string.IsNullOrEmpty(storedDb))
        {
            var db = storedDb.EndsWith(".IBS", StringComparison.OrdinalIgnoreCase) ? storedDb : storedDb + ".IBS";
            await CreateDbIfNeededAsync(db, ct);
            return db;
        }
        _logger?.LogInformation("GetOrCreateCurrentDbAsync: создаём БД {Db} для даты {Date}", dbFileName, today);
        await CreateDbIfNeededAsync(dbFileName, ct);
        try { await _settings.SetCurrentDbAsync(dbFileName, today); } catch { /* нет Blazor circuit — ок для folder watch */ }
        return dbFileName;
    }

    private async Task CreateDbIfNeededAsync(string name, CancellationToken ct)
    {
        try
        {
            // Прямой вызов DbManager — без HTTP / localStorage :5060
            var (ok, msg) = await _dbManager.CreateFromArchiveAsync(name, ct);
            if (ok)
            {
                _logger?.LogInformation("CreateDbIfNeededAsync: БД {Name} создана", name);
                return;
            }
            if (msg.Contains("уже", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug("CreateDbIfNeededAsync: БД {Name} уже существует", name);
                return;
            }
            _logger?.LogWarning("CreateDbIfNeededAsync: не удалось создать {Name}: {Msg}", name, msg);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "CreateDbIfNeededAsync failed for {Name}", name);
        }
    }

    public async Task<bool> SaveRecordAsync(RecordDto record, CancellationToken ct = default)
    {
        var baseUrl = await _settings.GetRecordsApiBaseUrlAsync();
        if (string.IsNullOrEmpty(baseUrl))
        {
            _logger?.LogWarning("SaveRecordAsync: baseUrl пуст, API записей не настроен");
            return false;
        }
        try
        {
            var url = $"{baseUrl}/api/records";
            _logger?.LogInformation("SaveRecordAsync: POST {Url}, carNumber={Car}, db={Db}, screenshotBase64={ScreenshotStatus} (len={Len}), lat={Lat}, lon={Lon}, deviceId={DeviceId}, source={Source}",
                url, record.CarNumber, record.Db,
                string.IsNullOrEmpty(record.ScreenshotBase64) ? "NULL/EMPTY" : "PRESENT",
                record.ScreenshotBase64?.Length ?? 0,
                record.Latitude, record.Longitude, record.DeviceId, record.Source);
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = JsonContent.Create(record);
            var gpsUrl = await _settings.GetGpsApiBaseUrlAsync();
            if (!string.IsNullOrEmpty(gpsUrl))
                req.Headers.TryAddWithoutValidation("X-Gps-Api-Base-Url", gpsUrl);
            var response = await _http.SendAsync(req, ct);
            var ok = response.IsSuccessStatusCode;
            if (!ok)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogWarning("SaveRecordAsync: HTTP {Status}, body={Body}", response.StatusCode, body.Length > 200 ? body[..200] + "..." : body);
            }
            else
            {
                var respBody = await response.Content.ReadAsStringAsync(ct);
                _logger?.LogInformation("SaveRecordAsync: OK, carNumber={Car}, response={Resp}", record.CarNumber, respBody);
            }
            return ok;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SaveRecordAsync: ошибка carNumber={Car}", record.CarNumber);
            return false;
        }
    }

    public async Task<ProcessVideoResponse?> ProcessVideoFromPathAsync(
        string videoPath,
        double sampleFps,
        IProgress<VideoProcessProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            // В том же процессе (без HTTP на старый :5060 из localStorage)
            return await RunVideoProcessorAsync(videoPath, sampleFps, progress, ct);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ProcessVideoFromPathAsync failed");
            return null;
        }
    }

    public async Task<ProcessVideoResponse?> ProcessVideoAsync(
        Stream videoStream,
        string fileName,
        double sampleFps,
        IProgress<VideoProcessProgress>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nomeroff_upload_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) ext = ".mp4";
            var videoPath = Path.Combine(tempDir, "video" + ext);

            progress?.Report(new VideoProcessProgress
            {
                Stage = "upload",
                Message = "Сохранение видео...",
                Percent = 0
            });

            await using (var fs = File.Create(videoPath))
                await videoStream.CopyToAsync(fs, ct);

            return await RunVideoProcessorAsync(videoPath, sampleFps, progress, ct);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ProcessVideoAsync failed");
            return null;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    private async Task<ProcessVideoResponse?> RunVideoProcessorAsync(
        string videoPath,
        double sampleFps,
        IProgress<VideoProcessProgress>? progress,
        CancellationToken ct)
    {
        ProcessVideoResponse? result = null;
        string? error = null;

        await _videoProcessor.ProcessAsync(
            videoPath,
            new VideoProcessOptions { SampleFps = sampleFps },
            async (payload, token) =>
            {
                if (payload is VideoProcessEmitResult typed)
                {
                    result = VideoEmitMapper.ToResponse(typed);
                    _logger?.LogInformation(
                        "ProcessVideo result: frames={Frames}, gpsFrames={Gps}/{GpsOk}, plates={Plates}",
                        result.Results.Count,
                        result.Results.Count(r => r.Latitude.HasValue && r.Longitude.HasValue),
                        typed.GpsOkCount,
                        result.Results.Sum(r => r.Plates.Count));
                    return;
                }

                var line = JsonSerializer.Serialize(payload);
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type == "progress")
                {
                    progress?.Report(new VideoProcessProgress
                    {
                        Stage = root.TryGetProperty("stage", out var s) ? s.GetString() ?? "" : "",
                        Message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "",
                        Percent = root.TryGetProperty("percent", out var p) ? p.GetInt32() : 0,
                        Current = root.TryGetProperty("current", out var c) ? c.GetInt32() : 0,
                        Total = root.TryGetProperty("total", out var tot) ? tot.GetInt32() : 0
                    });
                }
                else if (type == "error")
                {
                    error = root.TryGetProperty("message", out var em) ? em.GetString() : "Ошибка обработки видео";
                    _logger?.LogWarning("ProcessVideo: error={Error}", error);
                }
                else if (type == "result")
                {
                    // Совместимость со старым NDJSON { type:result, ... }
                    result = JsonSerializer.Deserialize<ProcessVideoResponse>(line, VideoJsonOpts);
                    if (result != null)
                    {
                        var gpsN = result.Results.Count(r => r.Latitude.HasValue && r.Longitude.HasValue);
                        _logger?.LogInformation(
                            "ProcessVideo result(json): frames={Frames}, gpsFrames={Gps}, plates={Plates}",
                            result.Results.Count, gpsN,
                            result.Results.Sum(r => r.Plates.Count));
                    }
                }

                await Task.CompletedTask;
            },
            ct);

        if (error != null)
            throw new InvalidOperationException(error);

        return result;
    }
}
