using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nomeroff.Shared;

namespace Nomeroff.Video.Api;

/// <summary>Общая обработка локального видеофайла: ffmpeg → OCR → NDJSON-события.</summary>
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
        var tempDir = Path.Combine(Path.GetTempPath(), "nomeroff_video_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempDir);

            if (!File.Exists(videoPath))
                throw new FileNotFoundException("Видеофайл не найден", videoPath);

            await emit(new { type = "progress", stage = "save", message = "Подготовка файла...", percent = 2 }, ct);

            var framesDir = Path.Combine(tempDir, "frames");
            Directory.CreateDirectory(framesDir);
            var framePattern = Path.Combine(framesDir, "frame_%04d.jpg");
            var ffmpegPath = ResolveFfmpegPath();

            await emit(new { type = "progress", stage = "ffmpeg", message = "Извлечение кадров (ffmpeg)...", percent = 5 }, ct);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                ArgumentList = { "-y", "-i", videoPath, "-vf", $"fps=1/{intervalSec},scale=1920:-2", "-q:v", "2", framePattern },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                if (proc == null)
                {
                    await emit(new { type = "error", message = "Не удалось запустить ffmpeg. Проверьте путь к ffmpeg" }, ct);
                    return;
                }
                _logger.LogInformation("process-video: ffmpeg started, pid={Pid}, file={File}", proc.Id, videoPath);
                var stderr = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode != 0)
                {
                    _logger.LogError("process-video: ffmpeg exit {Code}. {Stderr}", proc.ExitCode, stderr);
                    await emit(new { type = "error", message = "ffmpeg завершился с ошибкой. Проверьте формат видео." }, ct);
                    return;
                }
            }

            var frameFiles = Directory.GetFiles(framesDir, "frame_*.jpg").OrderBy(f => f).ToList();
            var maxFrames = _config.GetValue<int?>("MaxVideoFrames") ?? 300;
            if (frameFiles.Count > maxFrames)
                frameFiles = frameFiles.Take(maxFrames).ToList();

            var total = frameFiles.Count;
            await emit(new
            {
                type = "progress",
                stage = "ocr",
                message = total == 0 ? "Кадры не извлечены" : $"Распознавание номеров: 0 / {total}",
                current = 0,
                total,
                percent = total == 0 ? 100 : 10
            }, ct);

            if (total == 0)
            {
                await emit(new { type = "result", totalFrames = 0, intervalSec, results = Array.Empty<object>() }, ct);
                return;
            }

            var http = _httpFactory.CreateClient("Nomeroff");
            var results = new List<object>();
            var frameIndex = 0;
            var gpsOkCount = 0;
            var plateCropRatio = Math.Clamp(_config.GetValue("GpsOcr:PlateCropBottomRatio", 0.12), 0, 0.45);
            var useRoiFallback = _config.GetValue("GpsOcr:PlateRoiFallback", true);

            foreach (var framePath in frameFiles)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await File.ReadAllBytesAsync(framePath, ct);
                var bytesForStore = FrameStoragePrep.CompressJpeg(bytes, maxWidth: 1280, jpegQuality: 72);
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

                var plateSet = new HashSet<string>(StringComparer.Ordinal);
                var ocrBytes = plateCropRatio > 0.001
                    ? PlateFramePrep.CropBottom(bytes, plateCropRatio)
                    : bytes;
                foreach (var p in await RecognizePlatesAsync(http, ocrBytes, ct))
                    plateSet.Add(p);

                if (useRoiFallback)
                {
                    var roiBytes = PlateFramePrep.RoadRoiUpscaled(bytes, bottomCropRatio: Math.Max(plateCropRatio, 0.12));
                    foreach (var p in await RecognizePlatesAsync(http, roiBytes, ct))
                        plateSet.Add(p);
                }

                if (plateSet.Count == 0 && plateCropRatio < 0.18)
                {
                    foreach (var p in await RecognizePlatesAsync(http, PlateFramePrep.CropBottom(bytes, 0.18), ct))
                        plateSet.Add(p);
                }

                var plates = plateSet.ToList();
                var timeSec = frameIndex * intervalSec;
                results.Add(new { timeSec, plates, imageBase64 = base64Full, latitude, longitude, overlayTimeUtc });
                frameIndex++;

                var pct = 10 + (int)(90.0 * frameIndex / total);
                await emit(new
                {
                    type = "progress",
                    stage = "ocr",
                    message = $"Распознавание номеров: {frameIndex} / {total}" + (plates.Count > 0 ? $" · найдено {plates.Count}" : ""),
                    current = frameIndex,
                    total,
                    percent = Math.Clamp(pct, 10, 100)
                }, ct);
            }

            if (_gpsOcr.IsAvailable)
                _logger.LogInformation("process-video: GPS OCR {GpsOk}/{Total}", gpsOkCount, results.Count);

            await emit(new { type = "progress", stage = "done", message = "Готово, отправка результата...", current = total, total, percent = 100 }, ct);
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

    private static string ResolveFfmpegPath()
    {
        var assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        var ffmpegPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
        if (!File.Exists(ffmpegPath))
            ffmpegPath = Path.GetFullPath(Path.Combine(assemblyLocation, "ffmpeg.exe"));
        return ffmpegPath;
    }

    private static async Task<List<string>> RecognizePlatesAsync(HttpClient http, byte[] jpegBytes, CancellationToken ct)
    {
        var body = new { image_base64 = Convert.ToBase64String(jpegBytes) };
        var response = await http.PostAsJsonAsync("api/process_frame", body, ct);
        if (!response.IsSuccessStatusCode)
            return new List<string>();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var plates = new List<string>();
        if (json.TryGetProperty("plates", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("plate", out var plate))
                {
                    var s = PlateAlphabet.LatinToCyrillic(plate.GetString());
                    if (!string.IsNullOrWhiteSpace(s) && PlateAlphabet.LooksLikeRuPlate(s))
                        plates.Add(PlateAlphabet.Normalize(s));
                }
            }
        }
        return plates;
    }
}
