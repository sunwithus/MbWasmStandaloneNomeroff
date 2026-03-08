using System.Text.Json;
using Microsoft.JSInterop;

namespace MbWasmStandaloneNomeroff.Services;

/// <summary>Настройки приложения (localStorage).</summary>
public class SettingsService
{
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
    private const string DefaultApiBaseUrl = "http://127.0.0.1:8000";
    private const string DefaultRecordsApiBaseUrl = "http://localhost:5000";
    private const string DefaultGpsApiBaseUrl = "http://localhost:5001";
    private const int DefaultCaptureIntervalMs = 1500;
    private const int DefaultDedupIntervalSec = 300;

    private readonly IJSRuntime _js;
    private string? _cachedApiBaseUrl;

    public SettingsService(IJSRuntime js) => _js = js;

    private const string KeySaveVideoToDb = "NomeroffSaveVideoToDb";

    public async Task<bool> GetSaveVideoFramesToDbAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settings.get", KeySaveVideoToDb);
            return v != "0" && v?.ToLowerInvariant() != "false";
        }
        catch { return true; } // по умолчанию включено
    }

    public async Task SetSaveVideoFramesToDbAsync(bool value)
    {
        await _js.InvokeVoidAsync("settings.set", KeySaveVideoToDb, value ? "1" : "0");
    }

    public async Task<string> GetApiBaseUrlAsync()
    {
        if (_cachedApiBaseUrl != null) return _cachedApiBaseUrl;
        try
        {
            var url = await _js.InvokeAsync<string?>("settings.get", KeyApiBaseUrl);
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
            var v = await _js.InvokeAsync<string?>("settings.get", KeyDeviceName);
            return string.IsNullOrWhiteSpace(v) ? "" : v.Trim();
        }
        catch { return ""; }
    }

    public async Task SetDeviceNameAsync(string name)
    {
        await _js.InvokeVoidAsync("settings.set", KeyDeviceName, name ?? "");
    }

    public async Task<string?> GetGpsPortAsync()
    {
        try { return await _js.InvokeAsync<string?>("settings.get", KeyGpsPort); }
        catch { return null; }
    }

    public async Task SetGpsPortAsync(string? port)
    {
        await _js.InvokeVoidAsync("settings.set", KeyGpsPort, port ?? "");
    }

    public async Task SetApiBaseUrlAsync(string url)
    {
        _cachedApiBaseUrl = string.IsNullOrWhiteSpace(url) ? DefaultApiBaseUrl : url.TrimEnd('/');
        await _js.InvokeVoidAsync("settings.set", KeyApiBaseUrl, _cachedApiBaseUrl);
    }

    public async Task<bool> GetAutoStartAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settings.get", KeyAutoStart);
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public async Task SetAutoStartAsync(bool value)
    {
        await _js.InvokeVoidAsync("settings.set", KeyAutoStart, value ? "1" : "0");
    }

    /// <summary>Watchlist хранится в C# (localStorage)</summary>
    public async Task<List<string>> GetWatchlistAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("settings.get", KeyWatchlist);
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            var list = JsonSerializer.Deserialize<List<string?>>(json);
            return list?.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList() ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    public async Task SetWatchlistAsync(IEnumerable<string> items)
    {
        var list = items?.Select(s => s?.Trim().ToUpperInvariant()).Where(s => !string.IsNullOrEmpty(s)).ToList() ?? new List<string>();
        var json = JsonSerializer.Serialize(list);
        await _js.InvokeVoidAsync("settings.set", KeyWatchlist, json);
    }

    public async Task<int> GetCaptureIntervalMsAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settings.get", KeyCaptureIntervalMs);
            if (string.IsNullOrWhiteSpace(v)) return DefaultCaptureIntervalMs;
            return int.TryParse(v, out var n) && n >= 500 && n <= 60000 ? n : DefaultCaptureIntervalMs;
        }
        catch { return DefaultCaptureIntervalMs; }
    }

    public async Task SetCaptureIntervalMsAsync(int ms)
    {
        await _js.InvokeVoidAsync("settings.set", KeyCaptureIntervalMs, Math.Clamp(ms, 500, 60000).ToString());
    }

    public async Task<string?> GetCameraDeviceIdAsync()
    {
        try
        {
            return await _js.InvokeAsync<string?>("settings.get", KeyCameraDeviceId);
        }
        catch { return null; }
    }

    public async Task SetCameraDeviceIdAsync(string? deviceId)
    {
        await _js.InvokeVoidAsync("settings.set", KeyCameraDeviceId, deviceId ?? "");
    }

    public async Task<string> GetRecordsApiBaseUrlAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settings.get", KeyRecordsApiBaseUrl);
            return string.IsNullOrWhiteSpace(v) ? DefaultRecordsApiBaseUrl : v.TrimEnd('/');
        }
        catch { return ""; }
    }

    public async Task SetRecordsApiBaseUrlAsync(string url)
    {
        await _js.InvokeVoidAsync("settings.set", KeyRecordsApiBaseUrl, string.IsNullOrWhiteSpace(url) ? DefaultRecordsApiBaseUrl : url.TrimEnd('/'));
    }

    public async Task<string> GetGpsApiBaseUrlAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settings.get", KeyGpsApiBaseUrl);
            return string.IsNullOrWhiteSpace(v) ? DefaultGpsApiBaseUrl : v.TrimEnd('/');
        }
        catch { return DefaultGpsApiBaseUrl; }
    }

    public async Task SetGpsApiBaseUrlAsync(string url)
    {
        await _js.InvokeVoidAsync("settings.set", KeyGpsApiBaseUrl, string.IsNullOrWhiteSpace(url) ? DefaultGpsApiBaseUrl : url.TrimEnd('/'));
    }

    public async Task<int> GetDedupIntervalSecAsync()
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settings.get", KeyDedupIntervalSec);
            if (string.IsNullOrWhiteSpace(v)) return DefaultDedupIntervalSec;
            return int.TryParse(v, out var n) && n >= 0 && n <= 86400 ? n : DefaultDedupIntervalSec;
        }
        catch { return DefaultDedupIntervalSec; }
    }

    public async Task SetDedupIntervalSecAsync(int sec)
    {
        await _js.InvokeVoidAsync("settings.set", KeyDedupIntervalSec, Math.Clamp(sec, 0, 86400).ToString());
    }

    public void InvalidateCache() => _cachedApiBaseUrl = null;
}
