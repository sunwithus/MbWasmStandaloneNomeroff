using Tesseract;

namespace Nomeroff.Video.Api;

internal sealed class TesseractOcrHelper : IDisposable
{
    private readonly TesseractEngine? _engine;
    private readonly ILogger _logger;

    public TesseractOcrHelper(GpsOcrOptions options, ILogger logger)
    {
        _logger = logger;
        var tessPath = Path.Combine(AppContext.BaseDirectory, options.TessDataSubdir);
        var trained = Path.Combine(tessPath, "eng.traineddata");
        if (!File.Exists(trained))
        {
            logger.LogWarning("Tesseract: нет {Path}", trained);
            return;
        }

        try
        {
            _engine = new TesseractEngine(tessPath, "eng", EngineMode.Default);
            // Пустой whitelist — ° ' и цифры распознаются лучше, чем с жёстким списком
            _engine.SetVariable("tessedit_char_whitelist", "");
            _engine.SetVariable("user_defined_dpi", "300");
            _engine.DefaultPageSegMode = PageSegMode.SingleLine;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tesseract: ошибка инициализации");
        }
    }

    public bool IsAvailable => _engine != null;

    public string Recognize(byte[] png)
    {
        if (_engine == null || png.Length == 0) return "";
        try
        {
            using var pix = Pix.LoadFromMemory(png);
            if (pix.Width < 80 || pix.Height < 40) return "";
            using var page = _engine.Process(pix, PageSegMode.SingleLine);
            return page.GetText() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tesseract OCR failed");
            return "";
        }
    }

    public void Dispose() => _engine?.Dispose();
}
