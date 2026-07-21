namespace Nomeroff.Video.Api;

/// <summary>Один кадр после OCR (типизированный, без анонимного JSON-roundtrip).</summary>
public sealed class VideoFrameEmit
{
    public double TimeSec { get; set; }
    public List<VideoPlateEmit> Plates { get; set; } = new();
    public string? ImageBase64 { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? OverlayTimeUtc { get; set; }
}

public sealed class VideoPlateEmit
{
    public string Plate { get; set; } = "";
    public double Confidence { get; set; } = 1.0;
    public string? PlateImageBase64 { get; set; }
}

public sealed class VideoProcessEmitResult
{
    public string Type { get; set; } = "result";
    public int TotalFrames { get; set; }
    public int IntervalSec { get; set; }
    public List<VideoFrameEmit> Results { get; set; } = new();
    public int GpsOkCount { get; set; }
}
