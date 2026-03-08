using System.Net.Http.Json;
using System.Text.Json;
using MbWasmStandaloneNomeroff.Models;

namespace MbWasmStandaloneNomeroff.Services;

/// <summary>Отправка записей в API БД (Interbase) и обработка видео через API.</summary>
public class RecordsService
{
    private readonly HttpClient _http;
    private readonly SettingsService _settings;

    public RecordsService(HttpClient http, SettingsService settings)
    {
        _http = http;
        _settings = settings;
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

    public async Task<(bool success, string message)> TestDbAsync(CancellationToken ct = default)
    {
        using var client = await GetRecordsApiClientAsync();
        if (client == null) return (false, "API записей не настроен.");
        try
        {
            var r = await client.GetFromJsonAsync<JsonElement>("api/db/test", ct);
            var ok = r.TryGetProperty("success", out var s) && s.GetBoolean();
            var msg = r.TryGetProperty("message", out var m) ? m.GetString() : null;
            return (ok, msg ?? (ok ? "OK" : "Ошибка"));
        }
        catch (Exception ex) { return (false, ex.Message); }
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

    /// <summary>Загрузить видео на Records API: ffmpeg разбивает на кадры, кадры уходят в Python API. URL Python передаётся в заголовке.</summary>
    public async Task<ProcessVideoResponse?> ProcessVideoAsync(Stream videoStream, string fileName, int intervalSec, CancellationToken ct = default)
    {
        var baseUrl = await _settings.GetRecordsApiBaseUrlAsync();
        if (string.IsNullOrEmpty(baseUrl)) return null;
        var nomeroffUrl = await _settings.GetApiBaseUrlAsync();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var content = new MultipartFormDataContent();
            content.Add(new StreamContent(videoStream), "file", fileName);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/process-video?intervalSec={intervalSec}")
            {
                Content = content
            };
            if (!string.IsNullOrEmpty(nomeroffUrl))
                req.Headers.TryAddWithoutValidation("X-Nomeroff-Api-Base-Url", nomeroffUrl);
            var response = await client.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<ProcessVideoResponse>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }
}
