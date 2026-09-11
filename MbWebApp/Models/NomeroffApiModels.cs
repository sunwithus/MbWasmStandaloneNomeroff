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
    [JsonPropertyName("device")]
    public string? Device { get; set; }
    [JsonPropertyName("device_name")]
    public string? DeviceName { get; set; }
    [JsonPropertyName("device_policy")]
    public string? DevicePolicy { get; set; }
    [JsonPropertyName("inference")]
    public string? Inference { get; set; }
    [JsonPropertyName("preprocess")]
    public string? Preprocess { get; set; }
    [JsonPropertyName("vram_used_mb")]
    public double? VramUsedMb { get; set; }
    [JsonPropertyName("vram_total_mb")]
    public double? VramTotalMb { get; set; }
    [JsonPropertyName("vram_allocated_mb")]
    public double? VramAllocatedMb { get; set; }
    [JsonPropertyName("vram_reserved_mb")]
    public double? VramReservedMb { get; set; }
    [JsonPropertyName("watchlist_size")]
    public int WatchlistSize { get; set; }

    public bool IsCuda =>
        string.Equals(Device, "cuda", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Inference, "cuda", StringComparison.OrdinalIgnoreCase);
}

public class PlateResult
{
    [JsonPropertyName("plate")]
    public string Plate { get; set; } = "";
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
    [JsonPropertyName("bbox")]
    public int[] Bbox { get; set; } = Array.Empty<int>();
    [JsonPropertyName("plate_image_base64")]
    public string? PlateImageBase64 { get; set; }
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

public class ProcessVideoPlateResult
{
    [JsonPropertyName("plate")]
    public string Plate { get; set; } = "";
    /// <summary>Score детектора: в кадре есть номерная пластина.</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 1.0;
    /// <summary>Уверенность CTC-головы OCR: текст прочитан верно.</summary>
    [JsonPropertyName("ocrConfidence")]
    public double OcrConfidence { get; set; }
    /// <summary>Вероятности по символам — веса для межкадрового голосования.</summary>
    [JsonPropertyName("charProbs")]
    public double[]? CharProbs { get; set; }
    [JsonPropertyName("bbox")]
    public int[]? Bbox { get; set; }
    [JsonPropertyName("bboxArea")]
    public int BboxArea { get; set; }
    [JsonPropertyName("plateImageBase64")]
    public string? PlateImageBase64 { get; set; }
}

public class ProcessVideoFrameResult
{
    [JsonPropertyName("timeSec")]
    public double TimeSec { get; set; }
    [JsonPropertyName("plates")]
    public List<ProcessVideoPlateResult> Plates { get; set; } = new();
    [JsonPropertyName("imageBase64")]
    public string? ImageBase64 { get; set; }
    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }
    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
    [JsonPropertyName("overlayTimeUtc")]
    public string? OverlayTimeUtc { get; set; }
}

public class ProcessVideoResponse
{
    [JsonPropertyName("totalFrames")]
    public int TotalFrames { get; set; }
    [JsonPropertyName("intervalSec")]
    public int IntervalSec { get; set; }
    [JsonPropertyName("sampleFps")]
    public double SampleFps { get; set; }
    [JsonPropertyName("results")]
    public List<ProcessVideoFrameResult> Results { get; set; } = new();
}

/// <summary>Событие прогресса из NDJSON-стрима POST /api/process-video</summary>
public class VideoProcessProgress
{
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public int Percent { get; set; }
    public int Current { get; set; }
    public int Total { get; set; }
}
