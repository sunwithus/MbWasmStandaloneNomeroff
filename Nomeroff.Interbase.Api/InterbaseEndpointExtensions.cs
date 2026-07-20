using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Nomeroff.Gps.Api;
using Nomeroff.Interbase.Api.Interbase;
using Nomeroff.Shared;

namespace Nomeroff.Interbase.Api;

public static class InterbaseServiceCollectionExtensions
{
    public static IServiceCollection AddNomeroffInterbase(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var dbFolder = Path.GetFullPath(Path.Combine(contentRoot, config["Interbase:DbFolder"] ?? "Examples"));
        var archivePath = Path.GetFullPath(Path.Combine(contentRoot, config["Interbase:ArchivePath"] ?? "empty38.zip"));
        Directory.CreateDirectory(dbFolder);
        services.AddSingleton(new DbManager(dbFolder, archivePath));

        var defaultConnStr = config["Interbase:ConnectionString"] ?? "";
        var generatorName = config["Interbase:GeneratorName"] ?? NomeroffInterbaseService.DefaultGeneratorName;
        services.AddSingleton(sp =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("NomeroffInterbase");
            return new NomeroffInterbaseService(defaultConnStr, logger, generatorName);
        });
        return services;
    }

    internal static NomeroffInterbaseService CreateService(string connStr, ILogger logger, IConfiguration config) =>
        new(connStr, logger, config["Interbase:GeneratorName"] ?? NomeroffInterbaseService.DefaultGeneratorName);
}

public static class InterbaseEndpointExtensions
{
    public static string GetConnectionString(string? db, DbManager dbManager, IConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(db))
            return dbManager.GetConnectionString(db);
        var connStr = config["Interbase:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(connStr))
            return connStr;
        var defaultDb = config["Interbase:DefaultDb"];
        if (!string.IsNullOrWhiteSpace(defaultDb))
            return dbManager.GetConnectionString(defaultDb);
        var list = dbManager.ListDatabases();
        if (list.Count > 0)
            return dbManager.GetConnectionString(list[0]);
        return "";
    }

    public static WebApplication MapNomeroffInterbaseEndpoints(this WebApplication app)
    {
        app.Logger.LogInformation("Interbase module registered");

        app.MapPost("/api/records", async (HttpContext ctx, [FromBody] RecordRequest req, IConfiguration config, NomeroffInterbaseService ibService, DbManager dbManager, IGpsService gpsService, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffInterbase");
            logger.LogInformation("/api/records: Db={Db}, CarNumber={CarNumber}, DeviceId={DeviceId}, Source={Source}, " +
                "ScreenshotBase64 is {ScreenshotStatus} (len={ScreenshotLen}), Lat={Lat}, Lon={Lon}",
                req.Db, req.CarNumber, req.DeviceId, req.Source,
                string.IsNullOrEmpty(req.ScreenshotBase64) ? "NULL/EMPTY" : "PRESENT",
                req.ScreenshotBase64?.Length ?? 0,
                req.Latitude, req.Longitude);

            var connStr = GetConnectionString(req.Db, dbManager, config);
            var service = InterbaseServiceCollectionExtensions.CreateService(connStr, logger, config);
            if (!service.IsConfigured)
            {
                logger.LogWarning("/api/records: БД не настроена, connStr пуст");
                return Results.Problem("Укажите БД или Interbase:ConnectionString в appsettings.json.");
            }

            byte[]? screenshotBlob = null;
            if (!string.IsNullOrEmpty(req.ScreenshotBase64))
            {
                try
                {
                    screenshotBlob = Convert.FromBase64String(req.ScreenshotBase64);
                    logger.LogInformation("/api/records: Base64 декодирован, размер изображения = {Size} байт", screenshotBlob.Length);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "/api/records: Ошибка декодирования Base64");
                }
            }
            else
            {
                logger.LogWarning("/api/records: ScreenshotBase64 пуст — изображение НЕ будет записано!");
            }

            var lat = req.Latitude;
            var lon = req.Longitude;
            if (!lat.HasValue || !lon.HasValue)
            {
                // Тот же процесс: берём GPS напрямую, без HTTP
                var local = gpsService.CurrentPosition;
                if (local != null)
                {
                    lat = local.Latitude;
                    lon = local.Longitude;
                    logger.LogInformation("/api/records: GPS из локального сервиса: lat={Lat}, lon={Lon}", lat, lon);
                }
                else
                {
                    var gpsUrl = ctx.Request.Headers["X-Gps-Api-Base-Url"].FirstOrDefault()?.TrimEnd('/');
                    if (!string.IsNullOrEmpty(gpsUrl))
                    {
                        try
                        {
                            using var gpsClient = new HttpClient { BaseAddress = new Uri(gpsUrl + "/"), Timeout = TimeSpan.FromSeconds(5) };
                            using var resp = await gpsClient.GetAsync("api/position");
                            var body = await resp.Content.ReadAsStringAsync();
                            if (resp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body) && body.Trim() != "null")
                            {
                                using var doc = JsonDocument.Parse(body);
                                var root = doc.RootElement;
                                if (root.TryGetProperty("latitude", out var latProp) && latProp.ValueKind == JsonValueKind.Number
                                    && root.TryGetProperty("longitude", out var lonProp) && lonProp.ValueKind == JsonValueKind.Number)
                                {
                                    lat = latProp.GetDouble();
                                    lon = lonProp.GetDouble();
                                    logger.LogInformation("/api/records: GPS получен по HTTP: lat={Lat}, lon={Lon}", lat, lon);
                                }
                                else
                                {
                                    logger.LogInformation("/api/records: GPS API ответил без координат (нет фикса)");
                                }
                            }
                            else
                            {
                                logger.LogInformation("/api/records: GPS API без позиции ({Status})", resp.StatusCode);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "/api/records: Не удалось получить GPS из {Url}", gpsUrl);
                        }
                    }
                }
            }

            var deviceId = req.DeviceId ?? config["Interbase:DefaultDeviceId"] ?? Environment.MachineName;
            var carNumber = PlateAlphabet.LatinToCyrillic(req.CarNumber);
            logger.LogInformation(
                "/api/records: вызов SaveRecordAsync: deviceId={DeviceId}, carNumber={CarNumber}, lat={Lat}, lon={Lon}, imageSize={Size}, timeUtc={TimeUtc}",
                deviceId, carNumber, lat, lon, screenshotBlob?.Length ?? 0, req.TimeUtc);
            var id = await service.SaveRecordAsync(deviceId, carNumber, lat, lon, screenshotBlob, timeUtc: req.TimeUtc);
            logger.LogInformation("/api/records: запись сохранена, id={Id}", id);
            return Results.Ok(new { id });
        });

        app.MapGet("/api/db/list", (DbManager dbManager) =>
        {
            var list = dbManager.ListDatabases();
            return Results.Ok(new { databases = list });
        });

        app.MapPost("/api/db/create", async ([FromBody] CreateDbRequest req, DbManager dbManager, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffInterbase");
            logger.LogInformation("api/db/create: name={Name}", req.Name);
            var (success, message) = await dbManager.CreateFromArchiveAsync(req.Name ?? "");
            return success ? Results.Ok(new { success = true, message }) : Results.BadRequest(new { success = false, message });
        });

        app.MapDelete("/api/db/delete", (string? db, DbManager dbManager, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffInterbase");
            logger.LogInformation("api/db/delete: db={Db}", db);
            if (string.IsNullOrWhiteSpace(db))
                return Results.BadRequest(new { success = false, message = "Укажите имя БД." });
            var (success, message) = dbManager.DeleteDatabase(db);
            return success ? Results.Ok(new { success = true, message }) : Results.BadRequest(new { success = false, message });
        });

        app.MapGet("/api/db/test", async (string? db, NomeroffInterbaseService defaultService, DbManager dbManager, IConfiguration config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffInterbase");
            var connStr = GetConnectionString(db, dbManager, config);
            var service = string.IsNullOrEmpty(db)
                ? defaultService
                : InterbaseServiceCollectionExtensions.CreateService(connStr, logger, config);
            if (!service.IsConfigured)
                return Results.Ok(new { success = false, message = "Выберите БД или настройте Interbase:ConnectionString." });
            var (success, message) = await service.TestConnectionAsync();
            return Results.Ok(new { success, message });
        });

        app.MapPost("/api/db/test-write", async (string? db, bool? withImage, NomeroffInterbaseService defaultService, DbManager dbManager, IConfiguration config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffInterbase");
            logger.LogInformation("test-write: db={Db}, withImage={WithImage}", db, withImage);
            var connStr = GetConnectionString(db, dbManager, config);
            var service = string.IsNullOrEmpty(db)
                ? defaultService
                : InterbaseServiceCollectionExtensions.CreateService(connStr, logger, config);
            if (!service.IsConfigured)
                return Results.Problem("Выберите БД.");
            try
            {
                byte[]? testImage = null;
                if (withImage != false)
                {
                    var imgPath = Path.Combine(dbManager.DbFolder, "1.jpg");
                    if (File.Exists(imgPath))
                        testImage = await File.ReadAllBytesAsync(imgPath);
                    if (testImage == null || testImage.Length == 0)
                        testImage = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAv/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBEQACEQD/ALH/2Q==");
                    if (testImage.Length > 1024 * 1024)
                    {
                        logger.LogWarning("test-write: 1.jpg слишком большой ({Size} байт), используем минимальный JPEG", testImage.Length);
                        testImage = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAv/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBEQACEQD/ALH/2Q==");
                    }
                }
                var id = await service.SaveRecordAsync("TEST_DEVICE", "А123ВС45", 55.7558, 37.6173, testImage);
                return Results.Ok(new { success = true, id });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "test-write: ошибка");
                return Results.Problem("Ошибка записи: " + ex.Message);
            }
        });

        app.MapGet("/api/db/records", async (string? db, int? limit, int? offset, NomeroffInterbaseService defaultService, DbManager dbManager, IConfiguration config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffInterbase");
            logger.LogInformation("api/db/records: db={Db}, limit={Limit}, offset={Offset}", db, limit, offset);
            var connStr = GetConnectionString(db, dbManager, config);
            var service = string.IsNullOrEmpty(db)
                ? defaultService
                : InterbaseServiceCollectionExtensions.CreateService(connStr, logger, config);
            if (!service.IsConfigured)
                return Results.Ok(new { records = Array.Empty<object>(), total = 0, message = "Выберите БД." });
            var l = limit ?? 100;
            var o = offset ?? 0;
            try
            {
                var (records, total) = await service.GetRecordsPageAsync(l, o);
                return Results.Ok(new { records, total, limit = l, offset = o });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "api/db/records failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapDelete("/api/db/records/{id:long}", async (long id, string? db, NomeroffInterbaseService defaultService, DbManager dbManager, IConfiguration config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffInterbase");
            logger.LogInformation("api/db/records DELETE: id={Id}, db={Db}", id, db);
            var connStr = GetConnectionString(db, dbManager, config);
            var service = string.IsNullOrEmpty(db)
                ? defaultService
                : InterbaseServiceCollectionExtensions.CreateService(connStr, logger, config);
            if (!service.IsConfigured)
                return Results.Problem("Выберите БД.");
            var ok = await service.DeleteRecordAsync(id);
            return ok ? Results.Ok(new { success = true }) : Results.Problem("Ошибка удаления.");
        });

        app.MapGet("/api/db/image/{id:long}", async (long id, string? db, NomeroffInterbaseService defaultService, DbManager dbManager, IConfiguration config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffInterbase");
            var connStr = GetConnectionString(db, dbManager, config);
            var service = string.IsNullOrEmpty(db)
                ? defaultService
                : InterbaseServiceCollectionExtensions.CreateService(connStr, logger, config);
            if (!service.IsConfigured)
                return Results.NotFound();
            var bytes = await service.GetImageAsync(id);
            if (bytes == null || bytes.Length == 0)
                return Results.NotFound();
            return Results.File(bytes, "image/jpeg");
        });

        app.MapGet("/ops/interbase", () =>
        {
            var html = new StringBuilder();
            html.AppendLine("<html><head><meta charset='utf-8'><title>Nomeroff Interbase</title></head><body style='color: #1C2A26; background-color: #F7F3EB;'>");
            html.AppendLine("<h2>База записей</h2><ul>");
            html.AppendLine("<li><a href='/ops/db' style='color: #0F6B5C;'>Просмотрщик БД</a></li>");
            html.AppendLine("<li><a href='/swagger' style='color: #0F6B5C;'>Swagger</a></li>");
            html.AppendLine("</ul></body></html>");
            return Results.Content(html.ToString(), "text/html; charset=utf-8");
        });

        app.MapGet("/ops/db", (HttpContext ctx, IWebHostEnvironment env) =>
        {
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var html = DbViewerHtml.GetHtml(baseUrl, env.WebRootPath ?? env.ContentRootPath);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        return app;
    }
}

public class RecordRequest
{
    [JsonPropertyName("db")]
    public string? Db { get; set; }
    [JsonPropertyName("screenshot_base64")]
    public string? ScreenshotBase64 { get; set; }
    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }
    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
    [JsonPropertyName("time_utc")]
    public string? TimeUtc { get; set; }
    [JsonPropertyName("carNumber")]
    public string? CarNumber { get; set; }
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
    [JsonPropertyName("source")]
    public string? Source { get; set; }
    [JsonPropertyName("reserved1")]
    public string? Reserved1 { get; set; }
    [JsonPropertyName("reserved2")]
    public string? Reserved2 { get; set; }
    [JsonPropertyName("reserved3")]
    public string? Reserved3 { get; set; }
}

public class CreateDbRequest
{
    public string? Name { get; set; }
}
