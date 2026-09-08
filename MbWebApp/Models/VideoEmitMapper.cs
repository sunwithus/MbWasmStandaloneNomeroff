using Nomeroff.Video.Api;

namespace MbWebApp.Models;

/// <summary>
/// Перевод типизированного результата видео-конвейера в модель ответа.
///
/// Раньше каждый потребитель делал это сам, и FolderWatchService падал на
/// регистре JSON-свойства: сериализация давала "Type", а чтение искало "type",
/// поэтому результат всегда терялся и файл уходил в disk-queue навсегда.
/// </summary>
public static class VideoEmitMapper
{
    public static ProcessVideoResponse ToResponse(VideoProcessEmitResult typed) => new()
    {
        TotalFrames = typed.TotalFrames,
        IntervalSec = typed.IntervalSec,
        SampleFps = typed.SampleFps,
        Results = typed.Results.Select(fr => new ProcessVideoFrameResult
        {
            TimeSec = fr.TimeSec,
            ImageBase64 = fr.ImageBase64,
            Latitude = fr.Latitude,
            Longitude = fr.Longitude,
            OverlayTimeUtc = fr.OverlayTimeUtc,
            Plates = fr.Plates.Select(p => new ProcessVideoPlateResult
            {
                Plate = p.Plate,
                Confidence = p.Confidence,
                OcrConfidence = p.OcrConfidence,
                CharProbs = p.CharProbs,
                Bbox = p.Bbox,
                BboxArea = p.BboxArea,
                PlateImageBase64 = p.PlateImageBase64
            }).ToList()
        }).ToList()
    };
}
