namespace Nomeroff.Interbase.Api;

/// <summary>Загрузка и подготовка HTML страницы просмотрщика БД из wwwroot/db-viewer.html.</summary>
public static class DbViewerHtml
{
    public static string GetHtml(string baseUrl, string webRootPath)
    {
        var path = Path.Combine(webRootPath, "db-viewer.html");
        var html = File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException("db-viewer.html не найден в wwwroot", path);
        var safeBaseUrl = baseUrl.Replace("\\", "\\\\").Replace("'", "\\'");
        return html.Replace("{baseUrl}", safeBaseUrl);
    }
}
