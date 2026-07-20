using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nomeroff.Shared;

namespace Nomeroff.Video.Api;

/// <summary>Общая обработка локального видеофайла: ffmpeg (чанки) → OCR → NDJSON-события.</summary>
public sealed class VideoFileProcessor
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly GpsOverlayOcr _gpsOcr;
    private readonly IConfiguration _config;
    private readonly ILogger<VideoFileProcessor> _logger;

    public VideoFileProcessor(
        IHttpClientFactory httpFactory,
        GpsOverlayOcr gpsOcr,
        IConfiguration config,
        ILogger<VideoFileProcessor> logger)
    {
        _httpFactory = httpFactory;
        _gpsOcr = gpsOcr;
        _config = config;
        _logger = logger;
    }

    public async Task ProcessAsync(
        string videoPath,
        int intervalSec,
        Func<object, CancellationToken, Task> emit,
        CancellationToken ct,
        bool deleteTempDirOnExit = true)
    {
        intervalSec = Math.Clamp(intervalSec, 1, 60);
        var chunkSize = Math.Clamp(_config.GetValue("MaxVideoFrames", 300), 1, 10_000);
        var minConfidence = Math.Clamp(_config.GetValue("PlateMinConfidence", 0.60), 0.0, 1.0);
        var tempDir = Path.Combine(Path.GetTempPath(), "nomeroff_video_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempDir);

            if (!File.Exists(videoPath))
                throw new FileNotFoundException("Видеофайл не найден", videoPath);

            await emit(new { type = "progress", stage = "save", message = "Подготовка файла...", percent = 2 }, ct);

            var ffmpegPath = ResolveFfmpegPath();
            var durationSec = await TryProbeDurationSecAsync(ffmpegPath, videoPath, ct);
            var estimatedFrames = durationSec.HasValue
                ? Math.Max(1, (int)Math.Ceiling(durationSec.Value / intervalSec))
                : (int?)null;

            await emit(new
            {
                type = "progress",
                stage = "ffmpeg",
                message = estimatedFrames.HasValue
                    ? $"Длинный ролик: ~{estimatedFrames} кадров, чанки по {chunkSize}..."
                    : $"Обработка чанками по {chunkSize} кадров...",
                percent = 5
            }, ct);

            var http = _httpFactory.CreateClient("Nomeroff");
            var results = new List<object>();
            var plateCropRatio = Math.Clamp(_config.GetValue("GpsOcr:PlateCropBottomRatio", 0.12), 0, 0.45);
            var useRoiFallback = _config.GetValue("GpsOcr:PlateRoiFallback", true);
            // ROI/агрессивный crop чаще дают мусор — выше порог
            var roiMinConfidence = Math.Clamp(
                _config.GetValue("PlateRoiMinConfidence", Math.Max(minConfidence, 0.72)),
                0.0, 1.0);
            var gpsOkCount = 0;
            var globalFrameIndex = 0;
            var chunkIndex = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var chunkStartSec = chunkIndex * chunkSize * intervalSec;
                if (durationSec.HasValue && chunkStartSec >= durationSec.Value - 0.05)
                    break;

                var chunkDir = Path.Combine(tempDir, $"chunk_{chunkIndex:D4}");
                Directory.CreateDirectory(chunkDir);
                var framePattern = Path.Combine(chunkDir, "frame_%04d.jpg");
                var chunkDurationSec = chunkSize * intervalSec;

                var extracted = await ExtractChunkAsync(
                    ffmpegPath, videoPath, chunkStartSec, chunkDurationSec, intervalSec, framePattern, emit, ct);
                if (!extracted)
                    return;

                var frameFiles = Directory.GetFiles(chunkDir, "frame_*.jpg").OrderBy(f => f).ToList();
                if (frameFiles.Count == 0)
                {
                    try { Directory.Delete(chunkDir, true); } catch { /* ignore */ }
                    break;
                }

                _logger.LogInformation(
                    "process-video: chunk {Chunk} start={Start}s frames={Count} (chunkSize={ChunkSize})",
                    chunkIndex, chunkStartSec, frameFiles.Count, chunkSize);

                foreach (var framePath in frameFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var bytes = await File.ReadAllBytesAsync(framePath, ct);
                    // Кадр для БД/UI: по умолчанию без даунскейла с 1920 и JPEG ~90 (читаемый номер).
                    // Раньше было 1280@72 — на глаз номер почти не читался.
                    var storeMaxWidth = _config.GetValue("StorageImage:MaxWidth", 1920);
                    var storeJpegQuality = _config.GetValue("StorageImage:JpegQuality", 90);
                    var bytesForStore = FrameStoragePrep.CompressJpeg(bytes, maxWidth: storeMaxWidth, jpegQuality: storeJpegQuality);
                    var base64Full = Convert.ToBase64String(bytesForStore);

                    double? latitude = null;
                    double? longitude = null;
                    string? overlayTimeUtc = null;
                    if (_gpsOcr.IsAvailable)
                    {
                        var overlay = await _gpsOcr.TryExtractFromFileAsync(framePath, ct);
                        latitude = overlay.Latitude;
                        longitude = overlay.Longitude;
                        if (overlay.OverlayTime is { } local)
                        {
                            var utc = DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
                            overlayTimeUtc = utc.ToString("O");
                        }
                        if (latitude.HasValue && longitude.HasValue)
                            gpsOkCount++;
                    }

                    // plate -> best confidence. Важно: всегда crop + ROI (+ full), иначе «кривой» crop
                    // блокирует ROI и теряются хорошо видимые номера.
                    var plateBest = new Dictionary<string, double>(StringComparer.Ordinal);
                    void Accept(IEnumerable<(string Plate, double Confidence)> found)
                    {
                        foreach (var (plate, conf) in found)
                        {
                            if (!plateBest.TryGetValue(plate, out var prev) || conf > prev)
                                plateBest[plate] = conf;
                        }
                    }

                    var ocrBytes = plateCropRatio > 0.001
                        ? PlateFramePrep.CropBottom(bytes, plateCropRatio)
                        : bytes;
                    Accept(await RecognizePlatesAsync(http, ocrBytes, minConfidence, ct));

                    // Полный кадр — иногда лучше ловит дальние/военные (не режем низ)
                    Accept(await RecognizePlatesAsync(http, bytes, minConfidence, ct));

                    if (useRoiFallback)
                    {
                        var roiBytes = PlateFramePrep.RoadRoiUpscaled(bytes, bottomCropRatio: Math.Max(plateCropRatio, 0.12));
                        Accept(await RecognizePlatesAsync(http, roiBytes, roiMinConfidence, ct));
                        Accept(await RecognizePlatesAsync(http, PlateFramePrep.ContrastBoost(roiBytes), roiMinConfidence, ct));
                        // Инверсия: военные номера (9036СС45 и т.п.)
                        Accept(await RecognizePlatesAsync(http, PlateFramePrep.Invert(roiBytes), minConfidence, ct, militaryLookalikeFix: true));
                    }

                    if (plateBest.Count == 0 && plateCropRatio < 0.18)
                    {
                        Accept(await RecognizePlatesAsync(http, PlateFramePrep.CropBottom(bytes, 0.18), minConfidence, ct));
                    }

                    var plates = PlateAlphabet.CollapseNearDuplicates(plateBest, maxDistance: 1);
                    var timeSec = globalFrameIndex * intervalSec;
                    results.Add(new { timeSec, plates, imageBase64 = base64Full, latitude, longitude, overlayTimeUtc });
                    globalFrameIndex++;

                    var totalForPct = estimatedFrames ?? Math.Max(globalFrameIndex, 1);
                    var pct = 10 + (int)(90.0 * Math.Min(globalFrameIndex, totalForPct) / totalForPct);
                    await emit(new
                    {
                        type = "progress",
                        stage = "ocr",
                        message = estimatedFrames.HasValue
                            ? $"Распознавание: {globalFrameIndex} / ~{estimatedFrames} (чанк {chunkIndex + 1})"
                            : $"Распознавание: {globalFrameIndex} (чанк {chunkIndex + 1})"
                              + (plates.Count > 0 ? $" · найдено {plates.Count}" : ""),
                        current = globalFrameIndex,
                        total = estimatedFrames ?? globalFrameIndex,
                        percent = Math.Clamp(pct, 10, 99)
                    }, ct);
                }

                try { Directory.Delete(chunkDir, true); } catch { /* ignore */ }

                if (frameFiles.Count < chunkSize)
                    break;

                chunkIndex++;
            }

            if (_gpsOcr.IsAvailable)
                _logger.LogInformation("process-video: GPS OCR {GpsOk}/{Total}", gpsOkCount, results.Count);

            if (results.Count == 0)
            {
                await emit(new { type = "result", totalFrames = 0, intervalSec, results = Array.Empty<object>() }, ct);
                return;
            }

            await emit(new { type = "progress", stage = "done", message = "Готово, отправка результата...", current = results.Count, total = results.Count, percent = 100 }, ct);
            await emit(new { type = "result", totalFrames = results.Count, intervalSec, results }, ct);
        }
        finally
        {
            if (deleteTempDirOnExit)
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }

    private async Task<bool> ExtractChunkAsync(
        string ffmpegPath,
        string videoPath,
        double startSec,
        double durationSec,
        int intervalSec,
        string framePattern,
        Func<object, CancellationToken, Task> emit,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        // -ss before -i: fast seek; -t limits chunk length
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(startSec.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(durationSec.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"fps=1/{intervalSec},scale=1920:-2");
        psi.ArgumentList.Add("-q:v");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add(framePattern);

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            await emit(new { type = "error", message = "Не удалось запустить ffmpeg. Проверьте путь к ffmpeg" }, ct);
            return false;
        }

        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            // Пустой хвост ролика иногда даёт ненулевой код при 0 кадров — не считаем фатальным
            var framesDir = Path.GetDirectoryName(framePattern);
            var any = framesDir != null && Directory.Exists(framesDir)
                && Directory.GetFiles(framesDir, "frame_*.jpg").Length > 0;
            if (!any)
            {
                _logger.LogDebug("process-video: ffmpeg chunk exit {Code} at {Start}s (no frames). {Stderr}",
                    proc.ExitCode, startSec, Truncate(stderr, 400));
                return true; // caller sees 0 frames and stops
            }

            _logger.LogError("process-video: ffmpeg exit {Code}. {Stderr}", proc.ExitCode, Truncate(stderr, 800));
            await emit(new { type = "error", message = "ffmpeg завершился с ошибкой. Проверьте формат видео." }, ct);
            return false;
        }

        return true;
    }

    private static async Task<double?> TryProbeDurationSecAsync(string ffmpegPath, string videoPath, CancellationToken ct)
    {
        try
        {
            var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
            if (!File.Exists(ffprobe))
                ffprobe = "ffprobe";

            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                ArgumentList =
                {
                    "-v", "error",
                    "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    videoPath
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0) return null;
            if (double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && sec > 0)
                return sec;
        }
        catch
        {
            // optional
        }
        return null;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "...";

    private static string ResolveFfmpegPath()
    {
        var assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        var ffmpegPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
        if (!File.Exists(ffmpegPath))
            ffmpegPath = Path.GetFullPath(Path.Combine(assemblyLocation, "ffmpeg.exe"));
        return ffmpegPath;
    }

    private static async Task<List<(string Plate, double Confidence)>> RecognizePlatesAsync(
        HttpClient http,
        byte[] jpegBytes,
        double minConfidence,
        CancellationToken ct,
        bool militaryLookalikeFix = false)
    {
        var body = new { image_base64 = Convert.ToBase64String(jpegBytes) };
        var response = await http.PostAsJsonAsync("api/process_frame", body, ct);
        if (!response.IsSuccessStatusCode)
            return new List<(string, double)>();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var plates = new List<(string, double)>();
        if (json.TryGetProperty("plates", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("plate", out var plate))
                    continue;

                var conf = 1.0;
                if (item.TryGetProperty("confidence", out var confEl) && confEl.ValueKind == JsonValueKind.Number)
                    conf = confEl.GetDouble();
                if (conf < minConfidence)
                    continue;

                var s = PlateAlphabet.Normalize(plate.GetString());
                if (string.IsNullOrWhiteSpace(s))
                    continue;

                // Е9036СС45 → 9036СС45
                var milLead = PlateAlphabet.TryFixMilitaryWithLeadingLetter(s);
                if (milLead != null)
                {
                    plates.Add((milLead, conf + 0.02)); // слегка предпочесть военный фикс
                    continue;
                }

                if (militaryLookalikeFix)
                {
                    var milLike = PlateAlphabet.TryFixMilitaryFromCivilianLookalike(s);
                    if (milLike != null)
                    {
                        plates.Add((milLike, conf + 0.02));
                        continue;
                    }
                }

                if (PlateAlphabet.LooksLikeRuPlate(s))
                    plates.Add((s, conf));
            }
        }
        return plates;
    }
}
