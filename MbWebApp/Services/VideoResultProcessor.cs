using MbWebApp.Models;
using Nomeroff.Shared;

namespace MbWebApp.Services;

public sealed class VideoPlateHit
{
    public string Plate { get; set; } = "";
    public double TimeSec { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool IsInWatchlist { get; set; }
    public bool IsDuplicate { get; set; }
    public double Confidence { get; set; }
    public double OcrConfidence { get; set; }
}

public sealed class VideoProcessSummary
{
    public int TotalFrames { get; set; }
    public int IntervalSec { get; set; }
    public int FramesWithPlates { get; set; }
    public int TotalPlates { get; set; }
    public int UniquePlates { get; set; }
    public int GpsFrames { get; set; }
    public int SavedWithGps { get; set; }
    public int SaveAttempts { get; set; }
    public int SaveFailures { get; set; }
    /// <summary>Треков, отброшенных как одноразовые чтения (фантомы).</summary>
    public int DroppedByFrameHits { get; set; }
    /// <summary>Треков, у которых голосование дало номер вне формата/региона РФ.</summary>
    public int DroppedByFormat { get; set; }
}

/// <summary>Итоговый номер трека — то, что уходит (или ушло бы) в БД одной записью.</summary>
public sealed class VideoTrackResult
{
    public string Plate { get; set; } = "";
    public double TimeSec { get; set; }
    public double Confidence { get; set; }
    public int FrameHits { get; set; }
    public string? TimeUtc { get; set; }
    public bool HasGps { get; set; }
}

public sealed class VideoResultProcessOutcome
{
    public List<VideoPlateHit> Hits { get; set; } = new();
    /// <summary>Одна запись на трек после голосования — в отличие от Hits с сырыми чтениями.</summary>
    public List<VideoTrackResult> Tracks { get; set; } = new();
    public VideoProcessSummary Summary { get; set; } = new();
    public List<string> WatchlistAlerts { get; set; } = new();
}

public sealed class VideoResultProcessRequest
{
    public required ProcessVideoResponse ApiResponse { get; set; }
    public bool SaveToDb { get; set; }
    public int DedupIntervalSec { get; set; } = 300;
    public bool SkipSaveWithoutGps { get; set; }
    public IReadOnlyList<string> Watchlist { get; set; } = Array.Empty<string>();
    public string? DeviceName { get; set; }
    public string Source { get; set; } = "video_file";
    /// <summary>Минимум разных кадров, в которых номер должен встретиться, чтобы попасть в БД.</summary>
    public int MinFrameHits { get; set; } = 2;
    public IProgress<(int current, int total, string message)>? Progress { get; set; }
}

/// <summary>
/// Сборка записей из кадров: чтения группируются в треки машин, номер трека
/// определяется голосованием по символам, а в БД уходит одна запись на трек.
///
/// Раньше побеждало чтение с максимальным score детектора, и одноразовые
/// фантомы (у них этот score как раз самый высокий) уезжали в БД наравне
/// с номерами, прочитанными по 3–5 раз.
/// </summary>
public class VideoResultProcessor
{
    private readonly RecordsService _records;
    private readonly PlateArbiter _arbiter;
    private readonly ILogger<VideoResultProcessor> _logger;
    private readonly bool _singleSightingEnabled;
    private readonly double _singleSightingMinOcr;

    public VideoResultProcessor(
        RecordsService records,
        PlateArbiter arbiter,
        IConfiguration config,
        ILogger<VideoResultProcessor> logger)
    {
        _records = records;
        _arbiter = arbiter;
        _logger = logger;
        _singleSightingEnabled = config.GetValue("PlateSingleSighting:Enabled", true);
        _singleSightingMinOcr = Math.Clamp(
            config.GetValue("PlateSingleSighting:MinOcrConfidence", 0.95), 0.0, 1.0);
    }

    public async Task<VideoResultProcessOutcome> ProcessAsync(
        VideoResultProcessRequest req,
        CancellationToken ct = default)
    {
        var api = req.ApiResponse;
        var outcome = new VideoResultProcessOutcome();
        var watchSet = req.Watchlist
            .Select(PlateAlphabet.Normalize)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.Ordinal);

        var minFrameHits = Math.Max(1, req.MinFrameHits);
        var tracker = new PlateTracker();
        double? lastLat = null;
        double? lastLon = null;
        var totalFrames = Math.Max(api.Results.Count, 1);
        var frameNo = 0;

        foreach (var fr in api.Results)
        {
            ct.ThrowIfCancellationRequested();
            frameNo++;
            req.Progress?.Report((frameNo, totalFrames,
                req.SaveToDb
                    ? $"Разбор кадров: {frameNo} / {totalFrames}"
                    : $"Обработка кадров: {frameNo} / {totalFrames}"));

            if (fr.Latitude.HasValue && fr.Longitude.HasValue)
            {
                lastLat = fr.Latitude;
                lastLon = fr.Longitude;
            }
            var frameLat = fr.Latitude ?? lastLat;
            var frameLon = fr.Longitude ?? lastLon;

            foreach (var plateItem in fr.Plates.Where(p => !string.IsNullOrWhiteSpace(p.Plate)))
            {
                var plateNorm = PlateAlphabet.Normalize(plateItem.Plate);
                if (!PlateAlphabet.LooksLikeRuPlate(plateNorm)
                    && PlateAlphabet.TryFixMilitaryWithLeadingLetter(plateNorm) is { } mil)
                    plateNorm = mil;
                if (plateNorm.Length == 0)
                    continue;

                var detConf = plateItem.Confidence > 0 ? plateItem.Confidence : 1.0;
                outcome.Hits.Add(new VideoPlateHit
                {
                    Plate = plateNorm,
                    TimeSec = fr.TimeSec,
                    Latitude = frameLat,
                    Longitude = frameLon,
                    IsInWatchlist = watchSet.Contains(plateNorm),
                    IsDuplicate = false,
                    Confidence = detConf,
                    OcrConfidence = plateItem.OcrConfidence
                });

                tracker.Add(new TrackDetection
                {
                    Plate = plateNorm,
                    CharProbs = plateItem.CharProbs ?? Array.Empty<double>(),
                    OcrConfidence = plateItem.OcrConfidence,
                    DetConfidence = detConf,
                    TimeSec = fr.TimeSec,
                    Bbox = plateItem.Bbox is { Length: >= 4 } ? plateItem.Bbox : null,
                    BboxArea = plateItem.BboxArea,
                    FrameImageBase64 = fr.ImageBase64,
                    PlateImageBase64 = plateItem.PlateImageBase64,
                    Latitude = frameLat,
                    Longitude = frameLon,
                    TimeUtc = fr.OverlayTimeUtc
                });
            }
        }

        var pending = BuildPendingSaves(tracker, minFrameHits, outcome.Summary);
        await ArbitrateLowConfidenceAsync(pending, ct);

        outcome.Tracks = pending.Select(p => new VideoTrackResult
        {
            Plate = p.Plate,
            TimeSec = p.TimeSec,
            Confidence = p.Confidence,
            FrameHits = p.FrameHits,
            TimeUtc = p.TimeUtc,
            HasGps = p.HasGps
        }).ToList();

        // Дубликат = не первое появление номера в ролике: помечаем для UI
        var seenPlates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in outcome.Hits.OrderBy(h => h.TimeSec))
            hit.IsDuplicate = !seenPlates.Add(PlateAlphabet.DedupStem(hit.Plate));

        foreach (var p in pending.Where(p => watchSet.Contains(p.Plate)))
            outcome.WatchlistAlerts.Add($"{p.Plate} (@{p.TimeSec:F1}с)");

        var savedWithGps = 0;
        var saveAttempts = 0;
        var saveFailures = 0;

        if (req.SaveToDb && pending.Count > 0)
        {
            var dbName = await _records.GetOrCreateCurrentDbAsync(ct);
            var i = 0;
            foreach (var p in pending)
            {
                ct.ThrowIfCancellationRequested();
                if (req.SkipSaveWithoutGps && !p.HasGps)
                    continue;

                i++;
                req.Progress?.Report((i, pending.Count, $"Сохранение в БД: {i} / {pending.Count}"));
                saveAttempts++;
                var dto = new RecordDto
                {
                    ScreenshotBase64 = p.ImageBase64,
                    PlateImageBase64 = p.PlateImageBase64,
                    Latitude = p.Lat,
                    Longitude = p.Lon,
                    TimeUtc = p.TimeUtc,
                    CarNumber = p.Plate,
                    Confidence = p.Confidence,
                    DeviceId = string.IsNullOrEmpty(req.DeviceName) ? null : req.DeviceName,
                    Source = req.Source,
                    Db = dbName
                };
                var saved = await _records.SaveRecordAsync(dto, ct);
                if (!saved) saveFailures++;
                else if (p.HasGps) savedWithGps++;
                _logger.LogInformation(
                    "Video save: plate={Plate}, vote={Conf:P0}, кадров={Hits}, saved={Saved}, gps={HasGps}, hasPlateCrop={HasCrop}",
                    p.Plate, p.Confidence, p.FrameHits, saved, p.HasGps, !string.IsNullOrEmpty(p.PlateImageBase64));
            }
        }

        outcome.Summary.TotalFrames = api.TotalFrames;
        outcome.Summary.IntervalSec = api.IntervalSec;
        outcome.Summary.FramesWithPlates = api.Results.Count(r => r.Plates.Any(p => !string.IsNullOrWhiteSpace(p.Plate)));
        outcome.Summary.TotalPlates = api.Results.Sum(r => r.Plates.Count(p => !string.IsNullOrWhiteSpace(p.Plate)));
        outcome.Summary.UniquePlates = pending.Count;
        outcome.Summary.GpsFrames = api.Results.Count(r => r.Latitude.HasValue && r.Longitude.HasValue);
        outcome.Summary.SavedWithGps = savedWithGps;
        outcome.Summary.SaveAttempts = saveAttempts;
        outcome.Summary.SaveFailures = saveFailures;
        return outcome;
    }

    private static double MaxOcrConfidence(PlateTrack track) =>
        track.Detections.Count == 0 ? 0 : track.Detections.Max(d => d.OcrConfidence);

    /// <summary>
    /// Исключение из правила «нужно 2 кадра»: машину видно один раз, но прочитана
    /// она уверенно и в валидном формате РФ.
    ///
    /// Порог по числу кадров ставился против фантомов, которые видно ровно раз.
    /// Но встречный поток на скорости даёт единственный кадр и у настоящих
    /// номеров — на замерах так терялся военный 9036СС45 при OCR 0.97. Одной
    /// уверенности мало (фантомы тоже бывают уверенными), поэтому требуется
    /// ещё и существующий код региона.
    /// </summary>
    private bool IsConfidentSingleSighting(PlateTrack track, string plate)
    {
        if (!_singleSightingEnabled)
            return false;
        if (!PlateAlphabet.LooksLikeRuPlateWithRegion(plate))
            return false;
        return MaxOcrConfidence(track) >= _singleSightingMinOcr;
    }

    private List<PendingTrackSave> BuildPendingSaves(
        PlateTracker tracker,
        int minFrameHits,
        VideoProcessSummary summary)
    {
        var pending = new List<PendingTrackSave>();
        foreach (var track in tracker.Tracks)
        {
            if (track.Detections.Count == 0)
                continue;

            var vote = PlateVote.Vote(track.Detections.Select(d => new PlateReading
            {
                Plate = d.Plate,
                CharProbs = d.CharProbs,
                OcrConfidence = d.OcrConfidence,
                TimeSec = d.TimeSec
            }));
            if (vote == null)
                continue;

            var plate = vote.Plate;
            if (!PlateAlphabet.LooksLikeRuPlateWithRegion(plate))
            {
                // Голосование может собрать номер, которого не бывает: чаще всего
                // это значит, что в трек попали чтения от разных машин.
                var fallback = track.Detections
                    .Where(d => PlateAlphabet.LooksLikeRuPlateWithRegion(d.Plate))
                    .OrderByDescending(d => d.OcrConfidence)
                    .FirstOrDefault();
                if (fallback == null)
                {
                    summary.DroppedByFormat++;
                    _logger.LogInformation(
                        "Трек #{Id}: голосование дало {Plate} вне формата РФ — не пишем", track.Id, plate);
                    continue;
                }
                plate = fallback.Plate;
            }

            if (track.FrameHits < minFrameHits && !IsConfidentSingleSighting(track, plate))
            {
                summary.DroppedByFrameHits++;
                _logger.LogInformation(
                    "Трек #{Id}: {Plate} встречен в {Hits} кадре(ах) < {Need}, OCR {Ocr:P0} — не пишем (похоже на фантом)",
                    track.Id, plate, track.FrameHits, minFrameHits, MaxOcrConfidence(track));
                continue;
            }

            // Кадр для фото — где номер крупнее всего, а не последний: к последнему
            // кадру машина уже уходит из поля зрения.
            var best = track.Best;
            var withGps = track.Detections.FirstOrDefault(d => d.Latitude.HasValue && d.Longitude.HasValue);
            pending.Add(new PendingTrackSave
            {
                Plate = plate,
                TimeSec = best.TimeSec,
                Lat = best.Latitude ?? withGps?.Latitude,
                Lon = best.Longitude ?? withGps?.Longitude,
                TimeUtc = best.TimeUtc,
                ImageBase64 = best.FrameImageBase64,
                PlateImageBase64 = best.PlateImageBase64,
                Confidence = vote.Confidence,
                FrameHits = track.FrameHits,
                HasGps = (best.Latitude ?? withGps?.Latitude).HasValue
                         && (best.Longitude ?? withGps?.Longitude).HasValue,
                Candidates = track.Detections
                    .Select(d => d.Plate)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            });
        }
        return pending.OrderBy(p => p.TimeSec).ToList();
    }

    /// <summary>
    /// Спорные треки — на второй каскад. Голосование не сошлось обычно там, где
    /// машина далеко или номер грязный; VLM смотрит кроп и выбирает из кандидатов.
    /// Выключено по умолчанию (VlmArbiter:Enabled).
    /// </summary>
    private async Task ArbitrateLowConfidenceAsync(
        List<PendingTrackSave> pending,
        CancellationToken ct)
    {
        if (!_arbiter.IsEnabled)
            return;

        var threshold = _arbiter.MinConfidence;
        foreach (var p in pending.Where(p => p.Confidence < threshold))
        {
            if (p.Candidates.Count <= 1)
                continue;

            var verdict = await _arbiter.TryArbitrateAsync(p.PlateImageBase64, p.Candidates, ct);
            if (string.IsNullOrEmpty(verdict) || verdict == p.Plate)
                continue;

            if (!PlateAlphabet.LooksLikeRuPlateWithRegion(verdict))
            {
                _logger.LogInformation(
                    "VLM-арбитр вернул {Plate} вне формата РФ — оставляем голосование {Voted}",
                    verdict, p.Plate);
                continue;
            }

            _logger.LogInformation(
                "VLM-арбитр: {Voted} -> {Plate} (голосование {Conf:P0} < {Threshold:P0})",
                p.Plate, verdict, p.Confidence, threshold);
            p.Plate = verdict;
        }
    }
}

internal sealed class PendingTrackSave
{
    public string Plate = "";
    public double TimeSec;
    public double? Lat;
    public double? Lon;
    public string? TimeUtc;
    public string? ImageBase64;
    public string? PlateImageBase64;
    public double Confidence;
    public int FrameHits;
    public bool HasGps;
    /// <summary>Разные чтения этого трека — кандидаты для VLM-арбитра.</summary>
    public List<string> Candidates = new();
}
