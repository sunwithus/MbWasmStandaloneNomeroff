using System.Text.Json.Serialization;

namespace Nomeroff.Video.Api;

/// <summary>Один кадр после OCR (типизированный, без анонимного JSON-roundtrip).</summary>
public sealed class VideoFrameEmit
{
    public double TimeSec { get; set; }
    public List<VideoPlateEmit> Plates { get; set; } = new();
    public string? ImageBase64 { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Время кадра в UTC. Источник — имя файла + TimeSec, OSD как перекрёстная проверка.</summary>
    public string? OverlayTimeUtc { get; set; }
}

public sealed class VideoPlateEmit
{
    public string Plate { get; set; } = "";
    /// <summary>Score детектора YOLO: насколько это номер, а не насколько верно прочитан.</summary>
    public double Confidence { get; set; } = 1.0;
    /// <summary>Уверенность CTC-головы OCR — главный фильтр мусорных чтений.</summary>
    public double OcrConfidence { get; set; }
    /// <summary>Вероятности по символам из CTC — веса межкадрового голосования.</summary>
    public double[]? CharProbs { get; set; }
    /// <summary>Площадь bbox в исходном кадре: по ней выбирается кадр для фото.</summary>
    public int BboxArea { get; set; }
    public int[]? Bbox { get; set; }
    public string? PlateImageBase64 { get; set; }
}

public sealed class VideoProcessEmitResult
{
    // Регистр важен: NDJSON-стрим читают и camelCase-клиенты (браузер), и
    // типизированная ветка внутри процесса.
    [JsonPropertyName("type")]
    public string Type { get; set; } = "result";
    public int TotalFrames { get; set; }
    /// <summary>Оставлено для совместимости UI; фактическая плотность выборки — в SampleFps.</summary>
    public int IntervalSec { get; set; }
    public double SampleFps { get; set; }
    public List<VideoFrameEmit> Results { get; set; } = new();
    public int GpsOkCount { get; set; }
}
