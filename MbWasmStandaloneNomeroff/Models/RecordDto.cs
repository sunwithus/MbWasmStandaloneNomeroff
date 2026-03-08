using System.Text.Json.Serialization;

namespace MbWasmStandaloneNomeroff.Models;

/// <summary>Запись для сохранения в БД (POST в API записей).</summary>
public class RecordDto
{
    [JsonPropertyName("screenshot_base64")]
    public string? ScreenshotBase64 { get; set; }
    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }
    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
    [JsonPropertyName("time_utc")]
    public string? TimeUtc { get; set; }
    [JsonPropertyName("plate")]
    public string? Plate { get; set; }
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
    [JsonPropertyName("source")]
    public string? Source { get; set; }
    [JsonPropertyName("reserved1")]
    public string? Reserved1 { get; set; }
    [JsonPropertyName("reserved2")]
    public string? Reserved2 { get; set; }
    [JsonPropertyName("reserved3")]
    public string? Reserved3 { get; set; }
}
