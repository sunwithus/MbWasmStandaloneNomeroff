using System.Net.Http.Json;
using MbWebApp.Models;
using Microsoft.JSInterop;

namespace MbWebApp.Services;

public class NomeroffService
{
    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private readonly SettingsService _settings;

    public NomeroffService(HttpClient http, IJSRuntime js, SettingsService settings)
    {
        _http = http;
        _js = js;
        _settings = settings;
    }

    private async Task<string> GetBaseUrlAsync() => await _settings.GetApiBaseUrlAsync();

    public async Task<HealthResponse?> HealthAsync(CancellationToken ct = default)
    {
        var baseUrl = await GetBaseUrlAsync();
        try
        {
            var response = await _http.GetAsync($"{baseUrl}/health", ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken: ct);
        }
        catch { return null; }
    }

    public async Task<ProcessFrameResponse?> ProcessFrameAsync(string imageBase64, CancellationToken ct = default)
    {
        var baseUrl = await GetBaseUrlAsync();
        var request = new { image_base64 = imageBase64 };
        try
        {
            var response = await _http.PostAsJsonAsync($"{baseUrl}/api/process_frame", request, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<ProcessFrameResponse>(cancellationToken: ct);
        }
        catch { return null; }
    }

    public async Task<ProcessFrameResponse?> CaptureAndProcessAsync(string videoElementId, int maxWidth = 1280, CancellationToken ct = default)
    {
        var base64Image = await _js.InvokeAsync<string>("camera.captureFrame", videoElementId, maxWidth);
        if (string.IsNullOrEmpty(base64Image)) return null;
        return await ProcessFrameAsync(base64Image, ct);
    }

    public async Task PlayBeepAsync()
    {
        try { await _js.InvokeVoidAsync("audio.playBeep"); } catch { }
    }
}
