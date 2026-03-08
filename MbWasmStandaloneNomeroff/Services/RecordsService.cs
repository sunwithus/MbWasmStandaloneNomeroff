using System.Net.Http.Json;
using System.Text.Json;
using MbWasmStandaloneNomeroff.Models;

namespace MbWasmStandaloneNomeroff.Services;

/// <summary>Отправка записей в API БД (Interbase) и обработка видео через API.</summary>
public class RecordsService
{
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly ILogger<RecordsService>? _logger;

    public RecordsService(HttpClient http, SettingsService settings, ILogger<RecordsService>? logger = null)
    {
        _http = http;
        _settings = settings;
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

    /// <summary>Получить текущую позицию с GPS API (NMEA COM).</summary>
    public async Task<(double? lat, double? lon)> GetGpsPositionAsync(CancellationToken ct = default)
    {
        using var client = await GetGpsApiClientAsync();
        if (client == null) return (null, null);
        try
        {
            var r = await client.GetFromJsonAsync<JsonElement>("api/position", ct);
            if (r.TryGetProperty("latitude", out var latProp) && r.TryGetProperty("longitude", out var lonProp))
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

    /// <summary>Получить имя БД для текущей даты (yyyy-MM-dd.IBS). Создаёт БД при смене даты.</summary>
    public async Task<string?> GetOrCreateCurrentDbAsync(CancellationToken ct = default)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var dbFileName = today + ".IBS";
        var (storedDb, storedDate) = await _settings.GetCurrentDbAsync();
        if (storedDate == today && !string.IsNullOrEmpty(storedDb))
        {
            var db = storedDb.EndsWith(".IBS", StringComparison.OrdinalIgnoreCase) ? storedDb : storedDb + ".IBS";
            _logger?.LogDebug("GetOrCreateCurrentDbAsync: используем {Db}", db);
            return db;
        }
        _logger?.LogInformation("GetOrCreateCurrentDbAsync: создаём БД {Db} для даты {Date}", dbFileName, today);
        await CreateDbIfNeededAsync(dbFileName, ct);
        await _settings.SetCurrentDbAsync(dbFileName, today);
        return dbFileName;
    }

    private async Task CreateDbIfNeededAsync(string name, CancellationToken ct)
    {
        var baseUrl = await _settings.GetRecordsApiBaseUrlAsync();
        if (string.IsNullOrEmpty(baseUrl)) return;
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
            _logger?.LogDebug("CreateDbIfNeededAsync: POST api/db/create name={Name}", name);
            var resp = await client.PostAsJsonAsync("api/db/create", new { name }, ct);
            if (resp.IsSuccessStatusCode)
            {
                _logger?.LogInformation("CreateDbIfNeededAsync: БД {Name} создана", name);
                return;
            }
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            var msg = json.TryGetProperty("message", out var m) ? m.GetString() : "";
            if (msg?.Contains("уже", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger?.LogDebug("CreateDbIfNeededAsync: БД {Name} уже существует", name);
                return;
            }
            _logger?.LogWarning("CreateDbIfNeededAsync: не удалось создать {Name}, {Msg}", name, msg);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "CreateDbIfNeededAsync failed for {Name}", name);
        }
    }

    public async Task<bool> SaveRecordAsync(RecordDto record, CancellationToken ct = default)
    {
        var baseUrl = await _settings.GetRecordsApiBaseUrlAsync();
        if (string.IsNullOrEmpty(baseUrl)) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/records");
            req.Content = JsonContent.Create(record);
            var gpsUrl = await _settings.GetGpsApiBaseUrlAsync();
            if (!string.IsNullOrEmpty(gpsUrl))
                req.Headers.TryAddWithoutValidation("X-Gps-Api-Base-Url", gpsUrl);
            var response = await _http.SendAsync(req, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Загрузить видео на Nomeroff.Video.Api (ffmpeg → кадры → Python API).</summary>
    public async Task<ProcessVideoResponse?> ProcessVideoAsync(Stream videoStream, string fileName, int intervalSec, CancellationToken ct = default)
    {
        var baseUrl = await _settings.GetVideoApiBaseUrlAsync();
        if (string.IsNullOrEmpty(baseUrl)) return null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var content = new MultipartFormDataContent();
            content.Add(new StreamContent(videoStream), "file", fileName);
            var response = await client.PostAsync($"{baseUrl}/api/process-video?intervalSec={intervalSec}", content, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<ProcessVideoResponse>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }
}
