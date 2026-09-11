using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nomeroff.Shared;

namespace Nomeroff.Video.Api;

/// <summary>Настройки одного прогона видео.</summary>
public sealed class VideoProcessOptions
{
    /// <summary>Кадров в секунду на распознавание. Голосованию нужно &gt;= 2.</summary>
    public double SampleFps { get; set; } = 3.0;

    /// <summary>Устаревший вход из UI: 1/intervalSec. Задаёт SampleFps, если он не указан.</summary>
    public static VideoProcessOptions FromIntervalSec(int intervalSec) =>
        new() { SampleFps = intervalSec > 0 ? 1.0 / intervalSec : 1.0 };

    /// <summary>
    /// Исходное имя файла регистратора, если <c>videoPath</c> — временная копия
    /// или GUID из disk-queue. Нужно, чтобы вытащить дату съёмки из имени.
    /// </summary>
    public string? OriginalFileName { get; set; }
}

/// <summary>
/// Обработка локального видеофайла: ffmpeg (raw-пайп) → батчевый OCR → NDJSON-события.
///
/// Ключевые отличия от прежней схемы: кадры не уезжают на диск JPEG-ами и не
/// читаются обратно, на кадр приходится один HTTP-вызов вместо шести (варианты
/// предобработки делает Python), а декодирование и распознавание идут
/// одновременно через Channel, чтобы GPU не ждал, пока C# готовит следующий батч.
/// </summary>
public sealed class VideoFileProcessor
{
    private static readonly JsonSerializerOptions FrameMetaJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly GpsOverlayOcr _gpsOcr;
    private readonly IConfiguration _config;
    private readonly ILogger<VideoFileProcessor> _logger;
    // Старый OCR без /process_frames_raw: один 404 — дальше только JSON.
    private bool _rawFramesSupported = true;

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

    /// <summary>Совместимость с прежним вызовом по intervalSec.</summary>
    public Task ProcessAsync(
        string videoPath,
        int intervalSec,
        Func<object, CancellationToken, Task> emit,
        CancellationToken ct,
        bool deleteTempDirOnExit = true) =>
        ProcessAsync(videoPath, VideoProcessOptions.FromIntervalSec(intervalSec), emit, ct);

    public async Task ProcessAsync(
        string videoPath,
        VideoProcessOptions options,
        Func<object, CancellationToken, Task> emit,
        CancellationToken ct)
    {
        var sampleFps = Math.Clamp(
            options.SampleFps > 0 ? options.SampleFps : _config.GetValue("SampleFps", 3.0),
            0.05, 30.0);
        var batchSize = Math.Clamp(_config.GetValue("PlateBatchSize", 8), 1, 64);
        var minOcrConfidence = Math.Clamp(_config.GetValue("PlateOcrMinConfidence", 0.55), 0.0, 1.0);
        var minDetConfidence = Math.Clamp(_config.GetValue("PlateMinConfidence", 0.60), 0.0, 1.0);
        var osdEveryNth = Math.Max(1, _config.GetValue("GpsOcr:EveryNthFrame", 5));
        var osdDateTimeSamples = Math.Max(0, _config.GetValue("GpsOcr:DateTimeSamples", 3));
        // Ограничение на попытки, а не на успехи: если первые кадры OSD не
        // прочитались, дату всё равно надо добрать — иначе ролик без метки
        // времени в имени файла уезжает в БД со временем обработки.
        var osdDateTimeAttempts = Math.Max(
            osdDateTimeSamples, _config.GetValue("GpsOcr:DateTimeMaxAttempts", 12));
        var storeMaxWidth = _config.GetValue("StorageImage:MaxWidth", 1920);
        var storeJpegQuality = _config.GetValue("StorageImage:JpegQuality", 90);

        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Видеофайл не найден", videoPath);

        await emit(new { type = "progress", stage = "save", message = "Подготовка файла...", percent = 2 }, ct);

        var ffmpegPath = ResolveFfmpegPath();
        var ffprobePath = ResolveFfprobePath(ffmpegPath);
        var durationSec = await TryProbeDurationSecAsync(ffprobePath, videoPath, ct);
        // Запасная дата (имя файла → ffprobe), пока не прочитан OSD с кадра.
        var (startUtc, startSource) = await VideoTimestamps.ResolveFallbackStartUtcAsync(
            videoPath, ffprobePath, ct, options.OriginalFileName);
        _logger.LogInformation(
            "process-video: {File} fps={Fps} batch={Batch} запасная дата {Start:O} (источник {Source})",
            Path.GetFileName(videoPath), sampleFps, batchSize, startUtc, startSource);

        var estimatedFrames = durationSec.HasValue
            ? Math.Max(1, (int)Math.Ceiling(durationSec.Value * sampleFps))
            : (int?)null;

        await emit(new
        {
            type = "progress",
            stage = "ffmpeg",
            message = estimatedFrames.HasValue
                ? $"~{estimatedFrames} кадров при {sampleFps:0.##} fps, батчи по {batchSize}..."
                : $"Обработка батчами по {batchSize} кадров...",
            percent = 5
        }, ct);

        // Декодирование в одном потоке, распознавание в другом: пока GPU считает
        // батч, ffmpeg уже отдаёт следующие кадры.
        var channel = Channel.CreateBounded<RawFrame>(new BoundedChannelOptions(batchSize * 3)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(
            () => PumpFramesAsync(ffmpegPath, videoPath, sampleFps, channel.Writer, linked.Token),
            linked.Token);

        var http = _httpFactory.CreateClient("Nomeroff");
        var results = new List<VideoFrameEmit>();
        var gpsOkCount = 0;
        var frameIndex = 0;
        double? lastLat = null;
        double? lastLon = null;
        // Первое распознанное OSD-время с кадра — основной источник для БД.
        (DateTime Local, double TimeSec)? osdAnchor = null;
        var dateTimeHits = 0;
        var dateTimeTries = 0;

        try
        {
            var batch = new List<RawFrame>(batchSize);
            await foreach (var raw in channel.Reader.ReadAllAsync(ct))
            {
                batch.Add(raw);
                if (batch.Count < batchSize)
                    continue;

                await FlushBatchAsync(batch);
                batch.Clear();
            }
            if (batch.Count > 0)
                await FlushBatchAsync(batch);

            await producer;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            linked.Cancel();
            _logger.LogError(ex, "process-video: обработка прервана");
            await emit(new { type = "error", message = ex.Message }, ct);
            return;
        }

        if (_gpsOcr.IsAvailable)
            _logger.LogInformation("process-video: GPS OCR {GpsOk}/{Total}", gpsOkCount, results.Count);
        else
            _logger.LogWarning("process-video: GpsOcr выключен — в БД не будет координат из OSD");

        if (osdAnchor is { } anchor)
        {
            var osdStartUtc = VideoTimestamps.StartUtcFromOverlay(anchor.Local, anchor.TimeSec);
            _logger.LogInformation(
                "process-video: дата из OSD кадра {Osd:yyyy-MM-dd HH:mm:ss} @ {Sec:0.##}с → старт {New:O} (запасной был {Old} {OldStart:O})",
                anchor.Local, anchor.TimeSec, osdStartUtc, startSource, startUtc);
            startUtc = osdStartUtc;
            startSource = VideoStartSource.Overlay;
            foreach (var fr in results)
                fr.OverlayTimeUtc = startUtc.AddSeconds(fr.TimeSec).ToString("O");
        }
        else
        {
            _logger.LogWarning(
                "process-video: OSD с кадра не прочитался — в БД {Source} {Start:O}",
                startSource, startUtc);
            foreach (var fr in results)
                fr.OverlayTimeUtc = startUtc.AddSeconds(fr.TimeSec).ToString("O");
        }

        await emit(new
        {
            type = "progress", stage = "done", message = "Готово, отправка результата...",
            current = results.Count, total = results.Count, percent = 100
        }, ct);

        await emit(new VideoProcessEmitResult
        {
            TotalFrames = results.Count,
            IntervalSec = (int)Math.Max(1, Math.Round(1.0 / sampleFps)),
            SampleFps = sampleFps,
            Results = results,
            GpsOkCount = gpsOkCount
        }, ct);
        return;

        async Task FlushBatchAsync(List<RawFrame> frames)
        {
            var osd = new Dictionary<int, OverlayOcrResult>();
            foreach (var f in frames)
            {
                if (!_gpsOcr.IsAvailable || f.Index % osdEveryNth != 0)
                    continue;
                // Дату с левого нижнего угла читаем, пока не наберём якорь:
                // это основной источник S_DATETIME, не имя файла.
                var needDate = dateTimeHits < osdDateTimeSamples && dateTimeTries < osdDateTimeAttempts;
                if (needDate)
                    dateTimeTries++;
                var overlayOcr = await _gpsOcr.TryExtractFromBytesAsync(
                    f.Jpeg, $"t={f.TimeSec:0.##}s", needDate, ct);
                if (needDate && overlayOcr.OverlayTime.HasValue)
                    dateTimeHits++;
                osd[f.Index] = overlayOcr;
            }

            var recognized = await RecognizeBatchAsync(
                http, frames, minOcrConfidence, minDetConfidence, ct);

            for (var i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                if (osd.TryGetValue(f.Index, out var overlay))
                {
                    if (overlay.Latitude.HasValue) lastLat = overlay.Latitude;
                    if (overlay.Longitude.HasValue) lastLon = overlay.Longitude;
                    if (overlay.Latitude.HasValue && overlay.Longitude.HasValue)
                        gpsOkCount++;
                }

                var expectedUtc = startUtc.AddSeconds(f.TimeSec);
                var overlayTime = osd.TryGetValue(f.Index, out var o) ? o.OverlayTime : null;
                if (overlayTime.HasValue)
                {
                    osdAnchor ??= (overlayTime.Value, f.TimeSec);
                    if (!VideoTimestamps.OverlayAgreesWithExpected(overlayTime, expectedUtc))
                    {
                        _logger.LogWarning(
                            "OSD время {Osd:yyyy-MM-dd HH:mm:ss} расходится с расчётным {Expected:O}",
                            overlayTime.Value, expectedUtc);
                    }
                }

                results.Add(new VideoFrameEmit
                {
                    TimeSec = f.TimeSec,
                    Plates = i < recognized.Count ? recognized[i] : new List<VideoPlateEmit>(),
                    ImageBase64 = Convert.ToBase64String(
                        FrameStoragePrep.CompressJpeg(f.Jpeg, storeMaxWidth, storeJpegQuality)),
                    Latitude = lastLat,
                    Longitude = lastLon,
                    OverlayTimeUtc = expectedUtc.ToString("O")
                });
                frameIndex++;
            }

            var totalForPct = estimatedFrames ?? Math.Max(frameIndex, 1);
            var pct = 10 + (int)(90.0 * Math.Min(frameIndex, totalForPct) / totalForPct);
            var found = recognized.Sum(p => p.Count);
            await emit(new
            {
                type = "progress",
                stage = "ocr",
                message = estimatedFrames.HasValue
                    ? $"Распознавание: {frameIndex} / ~{estimatedFrames}"
                      + (found > 0 ? $" · найдено {found}" : "")
                    : $"Распознавание: {frameIndex}",
                current = frameIndex,
                total = estimatedFrames ?? frameIndex,
                percent = Math.Clamp(pct, 10, 99)
            }, ct);
        }
    }

    private sealed record RawFrame(int Index, double TimeSec, byte[] Jpeg);

    /// <summary>
    /// Один процесс ffmpeg на весь ролик: MJPEG в stdout вместо чанков файлов на диск.
    /// -hwaccel cuda снимает декодирование H.264 с CPU; при отсутствии CUDA-декодера
    /// ffmpeg сам откатывается на программный путь.
    /// </summary>
    private async Task PumpFramesAsync(
        string ffmpegPath,
        string videoPath,
        double sampleFps,
        ChannelWriter<RawFrame> writer,
        CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            if (_config.GetValue("FfmpegHwAccel", true))
            {
                // авто-детект: если CUDA-декодера нет, ffmpeg молча идёт софтом
                psi.ArgumentList.Add("-hwaccel");
                psi.ArgumentList.Add("auto");
            }
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(videoPath);
            // Без scale=1920:-2: источник уже 1920x1080, фильтр был no-op'ом,
            // но заставлял ffmpeg гонять кадры через swscale.
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"fps={sampleFps.ToString("0.####", CultureInfo.InvariantCulture)}");
            psi.ArgumentList.Add("-q:v");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("image2pipe");
            psi.ArgumentList.Add("-vcodec");
            psi.ArgumentList.Add("mjpeg");
            psi.ArgumentList.Add("pipe:1");

            using var proc = Process.Start(psi)
                             ?? throw new InvalidOperationException(
                                 "Не удалось запустить ffmpeg. Проверьте путь к ffmpeg");

            try
            {
                var stderrTask = proc.StandardError.ReadToEndAsync(ct);
                var index = 0;
                await foreach (var jpeg in ReadMjpegStreamAsync(proc.StandardOutput.BaseStream, ct))
                {
                    await writer.WriteAsync(new RawFrame(index, index / sampleFps, jpeg), ct);
                    index++;
                }

                await proc.WaitForExitAsync(ct);
                var stderr = await stderrTask;
                if (proc.ExitCode != 0 && index == 0)
                {
                    _logger.LogError("process-video: ffmpeg exit {Code}. {Stderr}",
                        proc.ExitCode, Truncate(stderr, 800));
                    failure = new InvalidOperationException(
                        "ffmpeg завершился с ошибкой. Проверьте формат видео.");
                }
                else if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _logger.LogDebug("process-video: ffmpeg stderr {Stderr}", Truncate(stderr, 400));
                }
            }
            finally
            {
                if (!proc.HasExited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    /// <summary>
    /// Разбор потока последовательных JPEG из image2pipe.
    ///
    /// В выводе ffmpeg -vcodec mjpeg нет ни EXIF-миниатюр, ни вложенных
    /// изображений, поэтому границы кадров однозначно задаются маркерами
    /// SOI (FF D8) и EOI (FF D9).
    /// </summary>
    internal static async IAsyncEnumerable<byte[]> ReadMjpegStreamAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        var frame = new MemoryStream(1 << 20);
        var inFrame = false;
        var prevWasFf = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read <= 0)
                break;

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (!inFrame)
                {
                    if (prevWasFf && b == 0xD8)
                    {
                        inFrame = true;
                        frame.SetLength(0);
                        frame.WriteByte(0xFF);
                        frame.WriteByte(0xD8);
                    }
                    prevWasFf = b == 0xFF;
                    continue;
                }

                frame.WriteByte(b);
                if (prevWasFf && b == 0xD9)
                {
                    inFrame = false;
                    prevWasFf = false;
                    yield return frame.ToArray();
                    continue;
                }
                prevWasFf = b == 0xFF;
            }
        }
    }

    /// <summary>
    /// Один HTTP-вызов на батч кадров: варианты предобработки (crop/ROI/контраст/
    /// инверсия) делает Python, поэтому 6 round-trip на кадр превращаются в 1 на батч.
    /// </summary>
    private async Task<List<List<VideoPlateEmit>>> RecognizeBatchAsync(
        HttpClient http,
        List<RawFrame> frames,
        double minOcrConfidence,
        double minDetConfidence,
        CancellationToken ct)
    {
        var empty = frames.Select(_ => new List<VideoPlateEmit>()).ToList();
        var variants = (_config["PlateVariants"] ?? "full,crop,roi,roi_contrast")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        HttpResponseMessage? response;
        try
        {
            response = await PostFramesAsync(
                http, frames, variants, minOcrConfidence, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "process_frames недоступен");
            return empty;
        }

        if (response == null)
            return empty;
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("process_frames HTTP {Status}", response.StatusCode);
                return empty;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!json.TryGetProperty("frames", out var framesEl) || framesEl.ValueKind != JsonValueKind.Array)
                return empty;

            var result = new List<List<VideoPlateEmit>>(frames.Count);
            foreach (var frameEl in framesEl.EnumerateArray())
            {
                var plates = new List<VideoPlateEmit>();
                if (frameEl.TryGetProperty("plates", out var platesEl)
                    && platesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in platesEl.EnumerateArray())
                    {
                        var emit = ReadPlate(p, minOcrConfidence, minDetConfidence);
                        if (emit != null)
                            plates.Add(emit);
                    }
                }
                result.Add(plates);
            }
            while (result.Count < frames.Count)
                result.Add(new List<VideoPlateEmit>());
            return result;
        }
    }

    /// <summary>
    /// Сначала сырые JPEG (без base64). Нет маршрута — JSON, как раньше.
    /// </summary>
    private async Task<HttpResponseMessage?> PostFramesAsync(
        HttpClient http,
        List<RawFrame> frames,
        string[] variants,
        double minOcrConfidence,
        CancellationToken ct)
    {
        if (_rawFramesSupported)
        {
            var raw = await TryPostFramesRawAsync(http, frames, variants, minOcrConfidence, ct);
            if (raw != null)
                return raw;
            _rawFramesSupported = false;
        }
        return await PostFramesJsonAsync(http, frames, variants, minOcrConfidence, ct);
    }

    private async Task<HttpResponseMessage?> TryPostFramesRawAsync(
        HttpClient http,
        List<RawFrame> frames,
        string[] variants,
        double minOcrConfidence,
        CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var meta = JsonSerializer.Serialize(new
        {
            variants,
            times_sec = frames.Select(f => f.TimeSec).ToArray(),
            min_ocr_confidence = minOcrConfidence,
            include_crop = true,
            min_frame_hits = 1
        }, FrameMetaJson);
        content.Add(new StringContent(meta, Encoding.UTF8), "meta");
        for (var i = 0; i < frames.Count; i++)
        {
            var part = new ByteArrayContent(frames[i].Jpeg);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(part, "files", $"{i}.jpg");
        }

        var resp = await http.PostAsync("api/process_frames_raw", content, ct);
        if (resp.StatusCode != HttpStatusCode.NotFound)
            return resp;

        resp.Dispose();
        _logger.LogInformation("process_frames_raw нет на OCR — дальше JSON/base64");
        return null;
    }

    private static Task<HttpResponseMessage> PostFramesJsonAsync(
        HttpClient http,
        List<RawFrame> frames,
        string[] variants,
        double minOcrConfidence,
        CancellationToken ct)
    {
        var body = new
        {
            frames = frames.Select(f => new
            {
                image_base64 = Convert.ToBase64String(f.Jpeg),
                time_sec = f.TimeSec
            }).ToArray(),
            variants,
            min_ocr_confidence = minOcrConfidence,
            include_crop = true,
            // Консенсус считаем в C# по всему ролику, а не по одному батчу.
            min_frame_hits = 1
        };
        return http.PostAsJsonAsync("api/process_frames", body, ct);
    }

    private static VideoPlateEmit? ReadPlate(JsonElement item, double minOcrConfidence, double minDetConfidence)
    {
        if (!item.TryGetProperty("plate", out var plateEl))
            return null;

        var plate = PlateAlphabet.Normalize(plateEl.GetString());
        if (string.IsNullOrWhiteSpace(plate) || !PlateAlphabet.LooksLikeRuPlateWithRegion(plate))
            return null;

        var ocrConf = GetDouble(item, "ocr_confidence");
        var detConf = GetDouble(item, "det_confidence", GetDouble(item, "confidence"));
        // Фильтруем по уверенности OCR: score детектора говорит лишь о том,
        // что в кадре есть номерная пластина, а не что текст прочитан верно.
        if (ocrConf < minOcrConfidence || detConf < minDetConfidence)
            return null;

        var bbox = ReadBbox(item);
        return new VideoPlateEmit
        {
            Plate = plate,
            Confidence = detConf,
            OcrConfidence = ocrConf,
            CharProbs = ReadCharProbs(item),
            Bbox = bbox,
            BboxArea = item.TryGetProperty("bbox_area", out var areaEl) && areaEl.ValueKind == JsonValueKind.Number
                ? areaEl.GetInt32()
                : BboxArea(bbox),
            PlateImageBase64 =
                item.TryGetProperty("plate_image_base64", out var cropEl) && cropEl.ValueKind == JsonValueKind.String
                    ? cropEl.GetString()
                    : null
        };
    }

    private static double[]? ReadCharProbs(JsonElement item)
    {
        if (!item.TryGetProperty("char_probs", out var el) || el.ValueKind != JsonValueKind.Array)
            return null;
        var values = new List<double>();
        foreach (var v in el.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.Number)
                values.Add(v.GetDouble());
        }
        return values.Count > 0 ? values.ToArray() : null;
    }

    private static int[]? ReadBbox(JsonElement item)
    {
        if (!item.TryGetProperty("bbox", out var bboxEl) || bboxEl.ValueKind != JsonValueKind.Array)
            return null;
        var bbox = new int[4];
        var i = 0;
        foreach (var v in bboxEl.EnumerateArray())
        {
            if (i >= 4) break;
            if (v.ValueKind == JsonValueKind.Number)
                bbox[i++] = v.GetInt32();
        }
        return i >= 4 ? bbox : null;
    }

    private static int BboxArea(int[]? bbox) =>
        bbox == null ? 0 : Math.Max(0, bbox[2] - bbox[0]) * Math.Max(0, bbox[3] - bbox[1]);

    private static double GetDouble(JsonElement item, string name, double fallback = 0.0) =>
        item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble()
            : fallback;

    private static async Task<double?> TryProbeDurationSecAsync(
        string ffprobePath, string videoPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
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
        foreach (var dir in CandidateToolDirs())
        {
            var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(ffmpeg))
                return Path.GetFullPath(ffmpeg);
        }
        return "ffmpeg";
    }

    private static string ResolveFfprobePath(string ffmpegPath)
    {
        var beside = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ffmpegPath)) ?? "", "ffprobe.exe");
        if (File.Exists(beside))
            return beside;
        foreach (var dir in CandidateToolDirs())
        {
            var ffprobe = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(ffprobe))
                return Path.GetFullPath(ffprobe);
        }
        return "ffprobe";
    }

    /// <summary>
    /// Каталог приложения, затем вверх по дереву — так находятся ffmpeg/ffprobe
    /// и в publish, и в корне репозитория (D:\_ANumberRecognition).
    /// </summary>
    private static IEnumerable<string> CandidateToolDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                 })
        {
            var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start);
            for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                if (seen.Add(dir.FullName))
                    yield return dir.FullName;
            }
        }
    }
}
