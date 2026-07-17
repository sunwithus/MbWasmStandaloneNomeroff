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
        var dedupCache = new Dictionary<string, VideoPlateDedupState>(StringComparer.Ordinal);
        var watchSet = req.Watchlist
            .Select(PlateAlphabet.Normalize)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.Ordinal);

        double? lastLat = null;
        double? lastLon = null;
        string? lastOverlayTimeUtc = null;
        var savedWithGps = 0;
        var totalFrames = Math.Max(api.Results.Count, 1);
        var frameNo = 0;

        foreach (var fr in api.Results)
        {
            ct.ThrowIfCancellationRequested();
            frameNo++;
            req.Progress?.Report((frameNo, totalFrames,
                req.SaveToDb
                    ? $"Обработка кадров и сохранение в БД: {frameNo} / {totalFrames}"
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

            foreach (var plate in fr.Plates.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var plateNorm = PlateAlphabet.Normalize(plate);
                var isInWatchlist = watchSet.Contains(plateNorm);
                var hasGps = frameLat.HasValue && frameLon.HasValue;
                VideoPlateDedup.TryGetState(dedupCache, plateNorm, out var dedupState);
                var withinDedup = VideoPlateDedup.IsWithinWindow(dedupState, fr.TimeSec, req.DedupIntervalSec);
                var isDup = VideoPlateDedup.IsDuplicateForDisplay(withinDedup, hasGps, dedupState);
                VideoPlateDedup.NoteSighting(dedupCache, plateNorm, fr.TimeSec);

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

                if (!VideoPlateDedup.ShouldSave(req.SaveToDb, withinDedup, hasGps, dedupState))
                    continue;
                if (req.SkipSaveWithoutGps && !hasGps)
                    continue;

                var timeUtc = !string.IsNullOrEmpty(fr.OverlayTimeUtc)
                    ? fr.OverlayTimeUtc
                    : !string.IsNullOrEmpty(lastOverlayTimeUtc)
                        ? lastOverlayTimeUtc
                        : DateTime.UtcNow.AddSeconds(-fr.TimeSec).ToString("O");

                var dbName = await _records.GetOrCreateCurrentDbAsync(ct);
                var dto = new RecordDto
                {
                    ScreenshotBase64 = fr.ImageBase64,
                    Latitude = frameLat,
                    Longitude = frameLon,
                    TimeUtc = timeUtc,
                    CarNumber = plateNorm,
                    DeviceId = string.IsNullOrEmpty(req.DeviceName) ? null : req.DeviceName,
                    Source = req.Source,
                    Db = dbName
                };

                var saved = await _records.SaveRecordAsync(dto, ct);
                if (saved && hasGps)
                {
                    savedWithGps++;
                    VideoPlateDedup.MarkSavedWithGps(dedupCache, plateNorm);
                }
                _logger.LogInformation("Video save: plate={Plate}, saved={Saved}, gps={HasGps}", plateNorm, saved, hasGps);
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
            SavedWithGps = savedWithGps
        };
        return outcome;
    }
}
