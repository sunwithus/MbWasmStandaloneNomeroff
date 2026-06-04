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

    public async Task<OverlayOcrResult> TryExtractFromFileAsync(string imagePath, CancellationToken ct = default)
    {
        if (!_enabled)
            return new OverlayOcrResult();

        var (leftPng, rightPng, fw, fh) = OverlayImagePrep.CreateStrips(imagePath, _options);
        string? usedEngine = null;
        string gpsText = "";
        string dateText = "";

        if (UseTesseract(_options.Engine) && _tesseract?.IsAvailable == true)
        {
            gpsText = _tesseract.Recognize(rightPng);
            dateText = _tesseract.Recognize(leftPng);
            usedEngine = "Tesseract";
        }

        var overlayTime = GpsOverlayOcrParser.ParseOverlayDateTime(dateText);
        var (lat, lon) = GpsOverlayOcrParser.ParseCoordinates(gpsText);

        if (!lat.HasValue && UsePython(_options.Engine))
        {
            var py = await TryPythonOcrAsync(rightPng, leftPng, ct);
            if (!string.IsNullOrWhiteSpace(py.Gps))
            {
                gpsText = py.Gps;
                usedEngine = "Python";
            }
            if (!string.IsNullOrWhiteSpace(py.Date))
                dateText = py.Date;
            overlayTime = GpsOverlayOcrParser.ParseOverlayDateTime(dateText);
            (lat, lon) = GpsOverlayOcrParser.ParseCoordinates(gpsText);
        }

        if (lat.HasValue)
        {
            _logger.LogInformation("GPS OCR ({Engine}): {File} {W}x{H} -> {Lat:F5}, {Lon:F5}, time={Time}",
                usedEngine, Path.GetFileName(imagePath), fw, fh, lat, lon,
                overlayTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-");
        }
        else if (_options.LogOcrTextOnMiss)
        {
            _logger.LogInformation("GPS OCR ({Engine}): {File} — нет GPS. RIGHT=[{Gps}] LEFT=[{Date}]",
                usedEngine ?? "none", Path.GetFileName(imagePath),
                Trim(gpsText), Trim(dateText));
        }

        return new OverlayOcrResult { Latitude = lat, Longitude = lon, OverlayTime = overlayTime };
    }

    private async Task<(string Gps, string Date)> TryPythonOcrAsync(byte[] rightPng, byte[] leftPng, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("Nomeroff");
            var body = new
            {
                gps_base64 = Convert.ToBase64String(rightPng),
                date_base64 = Convert.ToBase64String(leftPng)
            };
            var resp = await client.PostAsJsonAsync("api/ocr_overlay", body, ct);
            if (!resp.IsSuccessStatusCode) return ("", "");
            var json = await resp.Content.ReadFromJsonAsync<JsonOverlayOcrResponse>(cancellationToken: ct);
            return (json?.GpsText ?? "", json?.DateText ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Python ocr_overlay недоступен (pip install easyocr)");
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
