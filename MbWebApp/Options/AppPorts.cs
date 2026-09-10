using System.Text.RegularExpressions;

namespace MbWebApp.Options;

/// <summary>
/// Порты OCR и приложения. Источник — секция Ports в appsettings.json;
/// если её нет, берём из Urls / NomeroffApiBaseUrl.
/// </summary>
public static class AppPorts
{
    private static readonly Regex PortInUrl = new(
        @":(\d{2,5})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static int App(IConfiguration config) =>
        config.GetValue("Ports:App", ParsePort(config["Urls"], 5555));

    public static int Ocr(IConfiguration config) =>
        config.GetValue("Ports:Ocr", ParsePort(config["NomeroffApiBaseUrl"], 8000));

    public static string AppListenUrl(IConfiguration config) =>
        $"http://0.0.0.0:{App(config)}";

    public static string AppBaseUrl(IConfiguration config) =>
        $"http://127.0.0.1:{App(config)}";

    public static string OcrBaseUrl(IConfiguration config) =>
        $"http://127.0.0.1:{Ocr(config)}";

    private static int ParsePort(string? url, int fallback)
    {
        if (string.IsNullOrWhiteSpace(url))
            return fallback;
        var m = PortInUrl.Match(url);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) && p is > 0 and < 65536
            ? p
            : fallback;
    }
}
