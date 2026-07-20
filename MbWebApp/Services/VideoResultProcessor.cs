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
}

public sealed class VideoResultProcessOutcome
{
    public List<VideoPlateHit> Hits { get; set; } = new();
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
    public IProgress<(int current, int total, string message)>? Progress { get; set; }
}

internal sealed class PendingStemSave
{
    public string Plate = "";
    public double TimeSec;
    public double? Lat;
    public double? Lon;
    public string? TimeUtc;
    public string? ImageBase64;
    public bool HasGps;
}

/// <summary>
/// Сохранение в БД по стволу номера: за проезд пишем одно лучшее чтение
/// (длиннее регион предпочтительнее, иначе последнее), без привязки к скорости.
/// </summary>
public class VideoResultProcessor
{
    private readonly RecordsService _records;
    private readonly ILogger<VideoResultProcessor> _logger;

    public VideoResultProcessor(RecordsService records, ILogger<VideoResultProcessor> logger)
    {
        _records = records;
        _logger = logger;
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

        double? lastLat = null;
        double? lastLon = null;
        string? lastOverlayTimeUtc = null;
        var pending = new Dictionary<string, PendingStemSave>(StringComparer.Ordinal);
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
            if (!string.IsNullOrEmpty(fr.OverlayTimeUtc))
                lastOverlayTimeUtc = fr.OverlayTimeUtc;

            var frameLat = fr.Latitude ?? lastLat;
            var frameLon = fr.Longitude ?? lastLon;
            var hasGps = frameLat.HasValue && frameLon.HasValue;

            foreach (var plate in fr.Plates.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var plateNorm = PlateAlphabet.Normalize(plate);
                if (!PlateAlphabet.LooksLikeRuPlate(plateNorm)
                    && PlateAlphabet.TryFixMilitaryWithLeadingLetter(plateNorm) is { } mil)
                    plateNorm = mil;

                var isInWatchlist = watchSet.Contains(plateNorm);
                var stem = PlateAlphabet.DedupStem(plateNorm);
                var isDup = pending.ContainsKey(stem);

                outcome.Hits.Add(new VideoPlateHit
                {
                    Plate = plateNorm,
                    TimeSec = fr.TimeSec,
                    Latitude = frameLat,
                    Longitude = frameLon,
                    IsInWatchlist = isInWatchlist,
                    IsDuplicate = isDup
                });

                if (isInWatchlist && !isDup)
                    outcome.WatchlistAlerts.Add($"{plateNorm} (@{fr.TimeSec:F1}с)");

                if (!req.SaveToDb) continue;
                if (req.SkipSaveWithoutGps && !hasGps) continue;

                var timeUtc = !string.IsNullOrEmpty(fr.OverlayTimeUtc)
                    ? fr.OverlayTimeUtc
                    : !string.IsNullOrEmpty(lastOverlayTimeUtc)
                        ? lastOverlayTimeUtc
                        : DateTime.UtcNow.AddSeconds(-fr.TimeSec).ToString("O");

                if (!pending.TryGetValue(stem, out var cur))
                {
                    pending[stem] = new PendingStemSave
                    {
                        Plate = plateNorm,
                        TimeSec = fr.TimeSec,
                        Lat = frameLat,
                        Lon = frameLon,
                        TimeUtc = timeUtc,
                        ImageBase64 = fr.ImageBase64,
                        HasGps = hasGps
                    };
                    continue;
                }

                // Лучше: длиннее (полный регион) или то же длина — более позднее чтение
                var take = plateNorm.Length > cur.Plate.Length
                           || (plateNorm.Length == cur.Plate.Length && fr.TimeSec >= cur.TimeSec);
                if (!take) continue;

                cur.Plate = plateNorm;
                cur.TimeSec = fr.TimeSec;
                cur.Lat = frameLat;
                cur.Lon = frameLon;
                cur.TimeUtc = timeUtc;
                cur.ImageBase64 = fr.ImageBase64;
                cur.HasGps = hasGps;
            }
        }

        var savedWithGps = 0;
        var saveAttempts = 0;
        var saveFailures = 0;

        if (req.SaveToDb && pending.Count > 0)
        {
            var dbName = await _records.GetOrCreateCurrentDbAsync(ct);
            var i = 0;
            foreach (var kv in pending.OrderBy(p => p.Value.TimeSec))
            {
                ct.ThrowIfCancellationRequested();
                i++;
                req.Progress?.Report((i, pending.Count, $"Сохранение в БД: {i} / {pending.Count}"));
                var p = kv.Value;
                saveAttempts++;
                var dto = new RecordDto
                {
                    ScreenshotBase64 = p.ImageBase64,
                    Latitude = p.Lat,
                    Longitude = p.Lon,
                    TimeUtc = p.TimeUtc,
                    CarNumber = p.Plate,
                    DeviceId = string.IsNullOrEmpty(req.DeviceName) ? null : req.DeviceName,
                    Source = req.Source,
                    Db = dbName
                };
                var saved = await _records.SaveRecordAsync(dto, ct);
                if (!saved) saveFailures++;
                else if (p.HasGps) savedWithGps++;
                _logger.LogInformation("Video save: plate={Plate}, saved={Saved}, gps={HasGps}", p.Plate, saved, p.HasGps);
            }
        }

        outcome.Summary = new VideoProcessSummary
        {
            TotalFrames = api.TotalFrames,
            IntervalSec = api.IntervalSec,
            FramesWithPlates = api.Results.Count(r => r.Plates.Any(p => !string.IsNullOrWhiteSpace(p))),
            TotalPlates = api.Results.Sum(r => r.Plates.Count(p => !string.IsNullOrWhiteSpace(p))),
            UniquePlates = outcome.Hits.Select(v => v.Plate).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            GpsFrames = api.Results.Count(r => r.Latitude.HasValue && r.Longitude.HasValue),
            SavedWithGps = savedWithGps,
            SaveAttempts = saveAttempts,
            SaveFailures = saveFailures
        };
        return outcome;
    }
}
