using Microsoft.Extensions.Options;

namespace Nomeroff.Video.Api;

public sealed class GpsOverlayOcr : IDisposable
{
    private readonly GpsOcrOptions _options;
    private readonly ILogger<GpsOverlayOcr> _logger;
    private readonly TesseractOcrHelper? _tesseract;
    private readonly IHttpClientFactory _httpFactory;
    private readonly bool _enabled;

    public GpsOverlayOcr(
        IOptions<GpsOcrOptions> options,
        ILogger<GpsOverlayOcr> logger,
        IHttpClientFactory httpFactory)
    {
        _options = options.Value;
        _logger = logger;
        _httpFactory = httpFactory;
        _enabled = _options.Enabled;

        if (_enabled)
            _tesseract = new TesseractOcrHelper(_options, logger);

        if (_enabled)
        {
            _logger.LogInformation("GPS OCR: engine={Engine}, Tesseract={Tess}",
                _options.Engine, _tesseract?.IsAvailable ?? false);
        }
    }

    public bool IsAvailable => _enabled;

    public Task<OverlayOcrResult> TryExtractFromFileAsync(string imagePath, CancellationToken ct = default)
    {
        if (!_enabled)
            return Task.FromResult(new OverlayOcrResult());
        var strips = OverlayImagePrep.CreateStrips(imagePath, _options);
        return ExtractAsync(strips, Path.GetFileName(imagePath), needDateTime: true, ct);
    }

    /// <summary>
    /// Кадр из пайпа ffmpeg — без промежуточного файла на диске.
    ///
    /// needDateTime=true читает левую нижнюю полосу (дата/время регистратора).
    /// Это основной источник S_DATETIME. Выключаем полосу, когда якорь уже есть:
    /// дальше время кадра = якорь + TimeSec.
    /// </summary>
    public Task<OverlayOcrResult> TryExtractFromBytesAsync(
        byte[] jpegBytes, string label, bool needDateTime = true, CancellationToken ct = default)
    {
        if (!_enabled || jpegBytes.Length == 0)
            return Task.FromResult(new OverlayOcrResult());
        var strips = OverlayImagePrep.CreateStrips(jpegBytes, _options);
        return ExtractAsync(strips, label, needDateTime, ct);
    }

    private async Task<OverlayOcrResult> ExtractAsync(
        (byte[] LeftPng, byte[] RightPng, int FrameWidth, int FrameHeight) strips,
        string label,
        bool needDateTime,
        CancellationToken ct)
    {
        var (leftPng, rightPng, fw, fh) = strips;
        string? usedEngine = null;
        string gpsText = "";
        string dateText = "";

        // Auto/Python: RapidOCR в Python читает OSD регистратора надёжнее Tesseract
        if (UsePython(_options.Engine))
        {
            var py = await TryPythonOcrAsync(rightPng, needDateTime ? leftPng : null, ct);
            if (!string.IsNullOrWhiteSpace(py.Gps) || !string.IsNullOrWhiteSpace(py.Date))
            {
                gpsText = py.Gps;
                dateText = py.Date;
                usedEngine = "Python";
            }
        }

        var overlayTime = GpsOverlayOcrParser.ParseOverlayDateTime(
            string.IsNullOrWhiteSpace(dateText) ? gpsText : dateText);
        // GPS и дата иногда в одной строке RapidOCR — парсим оба текста
        var (lat, lon) = GpsOverlayOcrParser.ParseCoordinates(gpsText);
        if (!lat.HasValue)
            (lat, lon) = GpsOverlayOcrParser.ParseCoordinates(dateText);
        if (!lat.HasValue)
            (lat, lon) = GpsOverlayOcrParser.ParseCoordinates($"{gpsText} {dateText}");

        var needTessDate = needDateTime && !overlayTime.HasValue;
        if ((!lat.HasValue || needTessDate) && UseTesseract(_options.Engine) && _tesseract?.IsAvailable == true)
        {
            if (!lat.HasValue)
                gpsText = _tesseract.Recognize(rightPng);
            if (needTessDate)
                dateText = _tesseract.Recognize(leftPng);
            usedEngine = usedEngine == null ? "Tesseract" : usedEngine + "+Tesseract";
            overlayTime = GpsOverlayOcrParser.ParseOverlayDateTime(dateText) ?? overlayTime;
            if (!lat.HasValue)
                (lat, lon) = GpsOverlayOcrParser.ParseCoordinates(gpsText);
        }

        if (lat.HasValue || overlayTime.HasValue)
        {
            _logger.LogInformation("GPS OCR ({Engine}): {File} {W}x{H} -> {Lat}, {Lon}, time={Time}",
                usedEngine, label, fw, fh,
                lat.HasValue ? lat.Value.ToString("F5") : "-",
                lon.HasValue ? lon.Value.ToString("F5") : "-",
                overlayTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-");
        }
        else if (_options.LogOcrTextOnMiss)
        {
            _logger.LogInformation("GPS OCR ({Engine}): {File} — нет GPS/даты. RIGHT=[{Gps}] LEFT=[{Date}]",
                usedEngine ?? "none", label,
                Trim(gpsText), Trim(dateText));
        }

        return new OverlayOcrResult { Latitude = lat, Longitude = lon, OverlayTime = overlayTime };
    }

    private async Task<(string Gps, string Date)> TryPythonOcrAsync(byte[] rightPng, byte[]? leftPng, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("Nomeroff");
            var body = new
            {
                gps_base64 = Convert.ToBase64String(rightPng),
                date_base64 = leftPng is null ? "" : Convert.ToBase64String(leftPng)
            };
            var resp = await client.PostAsJsonAsync("api/ocr_overlay", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Python ocr_overlay HTTP {Status}", resp.StatusCode);
                return ("", "");
            }
            var json = await resp.Content.ReadFromJsonAsync<JsonOverlayOcrResponse>(cancellationToken: ct);
            return (json?.GpsText ?? "", json?.DateText ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Python ocr_overlay недоступен (pip install rapidocr-onnxruntime)");
            return ("", "");
        }
    }

    private static bool UseTesseract(string engine) =>
        engine.Equals("Tesseract", StringComparison.OrdinalIgnoreCase)
        || engine.Equals("Auto", StringComparison.OrdinalIgnoreCase);

    private static bool UsePython(string engine) =>
        engine.Equals("Python", StringComparison.OrdinalIgnoreCase)
        || engine.Equals("Auto", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string t)
    {
        t = t.Replace('\n', ' ').Trim();
        return t.Length > 120 ? t[..120] + "…" : t;
    }

    public void Dispose() => _tesseract?.Dispose();

    private sealed class JsonOverlayOcrResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("gps_text")]
        public string? GpsText { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("date_text")]
        public string? DateText { get; set; }
    }
}
