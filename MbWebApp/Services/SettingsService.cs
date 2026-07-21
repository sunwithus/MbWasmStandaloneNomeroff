using System.Text.Json;
using Microsoft.JSInterop;
using Microsoft.Extensions.Configuration;

namespace MbWebApp.Services;

public class SettingsService
{
    private readonly ILogger<SettingsService>? _logger;
    private readonly IConfiguration _configuration;
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

    private readonly string DefaultApiBaseUrl;
    private readonly string DefaultRecordsApiBaseUrl;
    private readonly string DefaultGpsApiBaseUrl;
    private readonly string DefaultVideoApiBaseUrl;
    private const int DefaultCaptureIntervalMs = 1500;
    private const int DefaultDedupIntervalSec = 300;

    private readonly IJSRuntime _js;
    private string? _cachedApiBaseUrl;

    public SettingsService(IJSRuntime js, IConfiguration configuration, ILogger<SettingsService>? logger = null)
    {
        _js = js;
        _configuration = configuration;
        _logger = logger;
        // Только appsettings / env — не localStorage (там часто старый :5060 после переноса)
        DefaultApiBaseUrl = (configuration["NomeroffApiBaseUrl"] ?? "http://127.0.0.1:8000").TrimEnd('/');
        var appBase = (configuration["AppBaseUrl"] ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(appBase))
        {
            var urls = configuration["Urls"] ?? "http://127.0.0.1:5555";
            appBase = urls.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal).TrimEnd('/');
        }
        DefaultRecordsApiBaseUrl = appBase;
        DefaultGpsApiBaseUrl = appBase;
        DefaultVideoApiBaseUrl = appBase;
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

    /// <summary>OCR URL из appsettings NomeroffApiBaseUrl (localStorage не используется).</summary>
    public Task<string> GetApiBaseUrlAsync()
    {
        _cachedApiBaseUrl = DefaultApiBaseUrl;
        return Task.FromResult(DefaultApiBaseUrl);
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

    /// <summary>Старые порты отдельных сервисов — игнорировать и переписать на дефолт.</summary>
    private static bool IsLegacyServiceUrl(string url) =>
        url.Contains(":5060", StringComparison.OrdinalIgnoreCase)
        || url.Contains(":5552", StringComparison.OrdinalIgnoreCase)
        || url.Contains(":5553", StringComparison.OrdinalIgnoreCase)
        || url.Contains(":5000", StringComparison.OrdinalIgnoreCase)
        || url.Contains(":5001", StringComparison.OrdinalIgnoreCase);

    private async Task<string> ResolveServiceUrlAsync(string key, string defaultUrl)
    {
        try
        {
            var v = await _js.InvokeAsync<string?>("settingsGet", key);
            if (string.IsNullOrWhiteSpace(v))
                return defaultUrl;
            v = v.TrimEnd('/');
            if (IsLegacyServiceUrl(v))
            {
                _logger?.LogWarning("localStorage {Key}={Url} — устаревший порт, сброс на {Default}", key, v, defaultUrl);
                try { await _js.InvokeVoidAsync("settingsSet", key, defaultUrl); } catch { /* ignore */ }
                return defaultUrl;
            }
            return v;
        }
        catch
        {
            return defaultUrl;
        }
    }

    /// <summary>Всегда из appsettings AppBaseUrl — localStorage игнорируется.</summary>
    public Task<string> GetRecordsApiBaseUrlAsync() => Task.FromResult(DefaultRecordsApiBaseUrl);

    public Task SetRecordsApiBaseUrlAsync(string url) => Task.CompletedTask;

    public Task<string> GetGpsApiBaseUrlAsync() => Task.FromResult(DefaultGpsApiBaseUrl);

    public Task SetGpsApiBaseUrlAsync(string url) => Task.CompletedTask;

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

    public Task<string> GetVideoApiBaseUrlAsync() => Task.FromResult(DefaultVideoApiBaseUrl);

    public Task SetVideoApiBaseUrlAsync(string url) => Task.CompletedTask;

    /// <summary>
    /// Сбросить URL OCR/GPS/БД/видео на дефолты из appsettings.
    /// Нужно после переноса: в localStorage могли остаться старые порты (5060, 5552…).
    /// </summary>
    public async Task ResetServiceUrlsToDefaultsAsync()
    {
        _cachedApiBaseUrl = DefaultApiBaseUrl;
        foreach (var key in new[] { KeyApiBaseUrl, KeyRecordsApiBaseUrl, KeyGpsApiBaseUrl, KeyVideoApiBaseUrl })
        {
            try { await _js.InvokeVoidAsync("settingsRemove", key); } catch { /* старый JS без remove */ }
        }
        await _js.InvokeVoidAsync("settingsSet", KeyApiBaseUrl, DefaultApiBaseUrl);
        await _js.InvokeVoidAsync("settingsSet", KeyRecordsApiBaseUrl, DefaultRecordsApiBaseUrl);
        await _js.InvokeVoidAsync("settingsSet", KeyGpsApiBaseUrl, DefaultGpsApiBaseUrl);
        await _js.InvokeVoidAsync("settingsSet", KeyVideoApiBaseUrl, DefaultVideoApiBaseUrl);
    }

    public (string Ocr, string App, string Video) GetDefaultServiceUrls() =>
        (DefaultApiBaseUrl, DefaultRecordsApiBaseUrl, DefaultVideoApiBaseUrl);

    public void InvalidateCache()
    {
        _cachedApiBaseUrl = null;
    }
}
