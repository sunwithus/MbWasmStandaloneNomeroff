using System.Text.Json.Serialization;

namespace MbWebApp.Models;

public class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
    [JsonPropertyName("model_loaded")]
    public bool ModelLoaded { get; set; }
    [JsonPropertyName("gpu_available")]
    public bool GpuAvailable { get; set; }
    [JsonPropertyName("watchlist_size")]
    public int WatchlistSize { get; set; }
}

public class PlateResult
{
    [JsonPropertyName("plate")]
    public string Plate { get; set; } = "";
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
    [JsonPropertyName("bbox")]
    public int[] Bbox { get; set; } = Array.Empty<int>();
    public bool IsInWatchlist { get; set; }
    public bool IsDuplicate { get; set; }
}

public class ProcessFrameResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("plates")]
    public List<PlateResult> Plates { get; set; } = new();
    [JsonPropertyName("processing_time_ms")]
    public double ProcessingTimeMs { get; set; }
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class WatchlistResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }
    [JsonPropertyName("items")]
    public List<string> Items { get; set; } = new();
}

public class WatchlistUpdateResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
    [JsonPropertyName("count")]
    public int Count { get; set; }
}

public class ProcessVideoFrameResult
{
    [JsonPropertyName("timeSec")]
    public double TimeSec { get; set; }
    [JsonPropertyName("plates")]
    public List<string> Plates { get; set; } = new();
    [JsonPropertyName("imageBase64")]
    public string? ImageBase64 { get; set; }
}

public class ProcessVideoResponse
{
    [JsonPropertyName("totalFrames")]
    public int TotalFrames { get; set; }
    [JsonPropertyName("intervalSec")]
    public int IntervalSec { get; set; }
    [JsonPropertyName("results")]
    public List<ProcessVideoFrameResult> Results { get; set; } = new();
}
