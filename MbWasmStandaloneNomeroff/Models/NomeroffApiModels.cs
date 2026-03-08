using System.Text.Json.Serialization;

namespace MbWasmStandaloneNomeroff.Models;

/// <summary>Ответ /health</summary>
public class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
    [JsonPropertyName("model_loaded")]
    public bool ModelLoaded { get; set; }
    [JsonPropertyName("gpu_available")]
    public bool GpuAvailable { get; set; }
    [JsonPropertyName("watchlist_size")]
    public int WatchlistSize { get; set; } // не используется (watchlist в C#)
}

/// <summary>Один распознанный номер</summary>
public class PlateResult
{
    [JsonPropertyName("plate")]
    public string Plate { get; set; } = "";
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
    [JsonPropertyName("bbox")]
    public int[] Bbox { get; set; } = Array.Empty<int>();
    /// <summary>Заполняется в C# по локальному watchlist.</summary>
    public bool IsInWatchlist { get; set; }
    /// <summary>Заполняется в C# по dedup-кэшу.</summary>
    public bool IsDuplicate { get; set; }
}

/// <summary>Ответ POST /api/process_frame</summary>
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

/// <summary>Ответ GET /api/watchlist</summary>
public class WatchlistResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }
    [JsonPropertyName("items")]
    public List<string> Items { get; set; } = new();
}

/// <summary>Ответ POST /api/watchlist</summary>
public class WatchlistUpdateResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>Один кадр в ответе POST /api/process-video (Records API)</summary>
public class ProcessVideoFrameResult
{
    [JsonPropertyName("timeSec")]
    public double TimeSec { get; set; }
    [JsonPropertyName("plates")]
    public List<string> Plates { get; set; } = new();
}

/// <summary>Ответ POST /api/process-video (Records API: ffmpeg + Python)</summary>
public class ProcessVideoResponse
{
    [JsonPropertyName("totalFrames")]
    public int TotalFrames { get; set; }
    [JsonPropertyName("intervalSec")]
    public int IntervalSec { get; set; }
    [JsonPropertyName("results")]
    public List<ProcessVideoFrameResult> Results { get; set; } = new();
}
