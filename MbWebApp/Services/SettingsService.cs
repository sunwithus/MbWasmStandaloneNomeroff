using System.Text.Json;
using Microsoft.JSInterop;

namespace MbWebApp.Services;

public class SettingsService
{
    private readonly ILogger<SettingsService>? _logger;
    private const string KeyApiBaseUrl = "NomeroffApiBaseUrl";
    private const string KeyDeviceName = "NomeroffDeviceName";
    private const string KeyGpsPort = "NomeroffGpsPort";
    private const string KeyAutoStart = "NomeroffAutoStart";
    private const string KeyWatchlist = "NomeroffWatchlist";
    private const string KeyCaptureIntervalMs = "NomeroffCaptureIntervalMs";
    private const string KeyCameraDeviceId = "NomeroffCameraDeviceId";
    private const string KeyRecordsApiBaseUrl = "NomeroffRecordsApiBaseUrl";
    private const string KeyGpsApiBaseUrl = "NomeroffGpsApiBaseUrl";
    private const string KeyDedupIntervalSec = "NomeroffDedupIntervalSec";
    private const string KeySkipSaveWithoutGps = "NomeroffSkipSaveWithoutGps";
    private const string KeyCurrentDb = "NomeroffCurrentDb";
    private const string KeyCurrentDbDate = "NomeroffCurrentDbDate";
    private const string KeyVideoApiBaseUrl = "NomeroffVideoApiBaseUrl";

    private const string DefaultApiBaseUrl = "http://127.0.0.1:8000";
    private const string DefaultRecordsApiBaseUrl = "http://localhost:5552";
    private const string DefaultGpsApiBaseUrl = "http://localhost:5551";
    private const string DefaultVideoApiBaseUrl = "http://localhost:5553";
    private const int DefaultCaptureIntervalMs = 1500;
    private const int DefaultDedupIntervalSec = 300;

    private readonly IJSRuntime _js;
    private string? _cachedApiBaseUrl;

    public SettingsService(IJSRuntime js, ILogger<SettingsService>? logger = null)
    {
        _js = js;
        _logger = logger;
    }

    private const string KeySaveVideoToDb = "NomeroffSaveVideoToDb";

    public async Task<bool> GetSaveVideoFramesToDbAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settingsGet", KeySaveVideoToDb);
            return v != "0" && v?.ToLowerInvariant() != "false";
        }
        catch { return true; }
    }

    public async Task SetSaveVideoFramesToDbAsync(bool value)
    {
        await _js.InvokeVoidAsync("settingsSet", KeySaveVideoToDb, value ? "1" : "0");
    }

    public async Task<string> GetApiBaseUrlAsync()
    {
        if (_cachedApiBaseUrl != null) return _cachedApiBaseUrl;
        try
        {
            var url = await _js.InvokeAsync<string?>("settingsGet", KeyApiBaseUrl);
            _cachedApiBaseUrl = string.IsNullOrWhiteSpace(url) ? DefaultApiBaseUrl : url.TrimEnd('/');
            return _cachedApiBaseUrl;
        }
        catch
        {
            _cachedApiBaseUrl = DefaultApiBaseUrl;
            return _cachedApiBaseUrl;
        }
    }

    public async Task<string> GetDeviceNameAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settingsGet", KeyDeviceName);
            return string.IsNullOrWhiteSpace(v) ? "" : v.Trim();
        }
        catch { return ""; }
    }

    public async Task SetDeviceNameAsync(string name)
    {
        await _js.InvokeVoidAsync("settingsSet", KeyDeviceName, name ?? "");
    }

    public async Task<string?> GetGpsPortAsync()
    {
        try { return await _js.InvokeAsync<string?>("settingsGet", KeyGpsPort); }
        catch { return null; }
    }

    public async Task SetGpsPortAsync(string? port)
    {
        await _js.InvokeVoidAsync("settingsSet", KeyGpsPort, port ?? "");
    }

    public async Task SetApiBaseUrlAsync(string url)
    {
        _cachedApiBaseUrl = string.IsNullOrWhiteSpace(url) ? DefaultApiBaseUrl : url.TrimEnd('/');
        await _js.InvokeVoidAsync("settingsSet", KeyApiBaseUrl, _cachedApiBaseUrl);
    }

    public async Task<bool> GetAutoStartAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settingsGet", KeyAutoStart);
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public async Task SetAutoStartAsync(bool value)
    {
        await _js.InvokeVoidAsync("settingsSet", KeyAutoStart, value ? "1" : "0");
    }

    public async Task<List<string>> GetWatchlistAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("settingsGet", KeyWatchlist);
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            var list = JsonSerializer.Deserialize<List<string?>>(json);
            return list?.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList() ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    public async Task SetWatchlistAsync(IEnumerable<string> items)
    {
        var list = items?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .ToList() ?? new List<string>();
        var json = JsonSerializer.Serialize(list);
        await _js.InvokeVoidAsync("settingsSet", KeyWatchlist, json);
    }

    public async Task<int> GetCaptureIntervalMsAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settingsGet", KeyCaptureIntervalMs);
            if (string.IsNullOrWhiteSpace(v)) return DefaultCaptureIntervalMs;
            return int.TryParse(v, out var n) && n >= 500 && n <= 60000 ? n : DefaultCaptureIntervalMs;
        }
        catch { return DefaultCaptureIntervalMs; }
    }

    public async Task SetCaptureIntervalMsAsync(int ms)
    {
        await _js.InvokeVoidAsync("settingsSet", KeyCaptureIntervalMs, Math.Clamp(ms, 500, 60000).ToString());
    }

    public async Task<string?> GetCameraDeviceIdAsync()
    {
        try
        {
            return await _js.InvokeAsync<string?>("settingsGet", KeyCameraDeviceId);
        }
        catch { return null; }
    }

    public async Task SetCameraDeviceIdAsync(string? deviceId)
    {
        await _js.InvokeVoidAsync("settingsSet", KeyCameraDeviceId, deviceId ?? "");
    }

    public async Task<string> GetRecordsApiBaseUrlAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settingsGet", KeyRecordsApiBaseUrl);
            return string.IsNullOrWhiteSpace(v) ? DefaultRecordsApiBaseUrl : v.TrimEnd('/');
        }
        catch { return ""; }
    }

    public async Task SetRecordsApiBaseUrlAsync(string url)
    {
        await _js.InvokeVoidAsync("settingsSet", KeyRecordsApiBaseUrl, string.IsNullOrWhiteSpace(url) ? DefaultRecordsApiBaseUrl : url.TrimEnd('/'));
    }

    public async Task<string> GetGpsApiBaseUrlAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settingsGet", KeyGpsApiBaseUrl);
            return string.IsNullOrWhiteSpace(v) ? DefaultGpsApiBaseUrl : v.TrimEnd('/');
        }
        catch { return DefaultGpsApiBaseUrl; }
    }

    public async Task SetGpsApiBaseUrlAsync(string url)
    {
        await _js.InvokeVoidAsync("settingsSet", KeyGpsApiBaseUrl, string.IsNullOrWhiteSpace(url) ? DefaultGpsApiBaseUrl : url.TrimEnd('/'));
    }

    public async Task<int> GetDedupIntervalSecAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settingsGet", KeyDedupIntervalSec);
            if (string.IsNullOrWhiteSpace(v)) return DefaultDedupIntervalSec;
            return int.TryParse(v, out var n) && n >= 0 && n <= 86400 ? n : DefaultDedupIntervalSec;
        }
        catch { return DefaultDedupIntervalSec; }
    }

    public async Task SetDedupIntervalSecAsync(int sec)
    {
        await _js.InvokeVoidAsync("settingsSet", KeyDedupIntervalSec, Math.Clamp(sec, 0, 86400).ToString());
    }

    public async Task<bool> GetSkipSaveWithoutGpsAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settingsGet", KeySkipSaveWithoutGps);
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public async Task SetSkipSaveWithoutGpsAsync(bool value)
    {
        await _js.InvokeVoidAsync("settingsSet", KeySkipSaveWithoutGps, value ? "1" : "0");
    }

    public async Task<(string? dbName, string? date)> GetCurrentDbAsync()
    {
        try
        {
            var db = await _js.InvokeAsync<string?>("settingsGet", KeyCurrentDb);
            var date = await _js.InvokeAsync<string?>("settingsGet", KeyCurrentDbDate);
            return (string.IsNullOrWhiteSpace(db) ? null : db.Trim(), string.IsNullOrWhiteSpace(date) ? null : date.Trim());
        }
        catch { return (null, null); }
    }

    public async Task SetCurrentDbAsync(string dbName, string date)
    {
        await _js.InvokeVoidAsync("settingsSet", KeyCurrentDb, dbName ?? "");
        await _js.InvokeVoidAsync("settingsSet", KeyCurrentDbDate, date ?? "");
    }

    public async Task<string> GetVideoApiBaseUrlAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settingsGet", KeyVideoApiBaseUrl);
            return string.IsNullOrWhiteSpace(v) ? DefaultVideoApiBaseUrl : v.TrimEnd('/');
        }
        catch { return DefaultVideoApiBaseUrl; }
    }

    public async Task SetVideoApiBaseUrlAsync(string url)
    {
        await _js.InvokeVoidAsync("settingsSet", KeyVideoApiBaseUrl, string.IsNullOrWhiteSpace(url) ? DefaultVideoApiBaseUrl : url.TrimEnd('/'));
    }

    public void InvalidateCache()
    {
        _cachedApiBaseUrl = null;
    }
}
