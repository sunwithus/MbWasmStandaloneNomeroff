namespace Nomeroff.Interbase.Api;

/// <summary>Загрузка и подготовка HTML страницы просмотрщика БД из wwwroot/db-viewer.html.</summary>
public static class DbViewerHtml
{
    public static string GetHtml(string baseUrl, string webRootPath)
    {
        var candidates = new[]
        {
            Path.Combine(webRootPath, "db-viewer.html"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "db-viewer.html"),
            Path.Combine(AppContext.BaseDirectory, "db-viewer.html"),
        };
        string? path = candidates.FirstOrDefault(File.Exists);
        if (path == null)
            throw new FileNotFoundException("db-viewer.html не найден в wwwroot", candidates[0]);
        var html = File.ReadAllText(path);
        var safeBaseUrl = baseUrl.Replace("\\", "\\\\").Replace("'", "\\'");
        return html.Replace("{baseUrl}", safeBaseUrl);
    }
}
