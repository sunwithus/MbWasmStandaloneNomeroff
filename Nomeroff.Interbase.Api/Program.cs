using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Nomeroff.Interbase.Api.Interbase;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Nomeroff.Interbase.Api", Version = "v1" });
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(pol =>
    {
        pol.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var root = builder.Environment.ContentRootPath;
var dbFolder = Path.Combine(root, builder.Configuration["Interbase:DbFolder"] ?? "Examples");
var archivePath = Path.GetFullPath(Path.Combine(root, builder.Configuration["Interbase:ArchivePath"] ?? "empty38.zip"));
builder.Services.AddSingleton(new DbManager(dbFolder, archivePath));

var defaultConnStr = builder.Configuration["Interbase:ConnectionString"] ?? "";
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("NomeroffInterbase");
    return new NomeroffInterbaseService(defaultConnStr, logger);
});

var app = builder.Build();

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Nomeroff.Interbase.Api v1");
});

// --- API для MbWasmStandaloneNomeroff ---
app.MapPost("/api/records", async (HttpContext ctx, [FromBody] RecordRequest req, IConfiguration config, NomeroffInterbaseService ibService, DbManager dbManager) =>
{
    var connStr = GetConnectionString(req.Db, dbManager, config);
    var service = new NomeroffInterbaseService(connStr);
    if (!service.IsConfigured)
        return Results.Problem("Укажите БД или Interbase:ConnectionString в appsettings.json.");

    byte[]? screenshotBlob = null;
    if (!string.IsNullOrEmpty(req.ScreenshotBase64))
    {
        try { screenshotBlob = Convert.FromBase64String(req.ScreenshotBase64); }
        catch { }
    }

    var lat = req.Latitude;
    var lon = req.Longitude;
    if (!lat.HasValue || !lon.HasValue)
    {
        var gpsUrl = ctx.Request.Headers["X-Gps-Api-Base-Url"].FirstOrDefault()?.TrimEnd('/');
        if (!string.IsNullOrEmpty(gpsUrl))
        {
            try
            {
                using var gpsClient = new HttpClient { BaseAddress = new Uri(gpsUrl + "/"), Timeout = TimeSpan.FromSeconds(5) };
                var posResp = await gpsClient.GetFromJsonAsync<JsonElement>("api/position");
                if (posResp.TryGetProperty("latitude", out var latProp) && posResp.TryGetProperty("longitude", out var lonProp))
                {
                    lat = latProp.GetDouble();
                    lon = lonProp.GetDouble();
                }
            }
            catch { }
        }
    }

    var deviceId = req.DeviceId ?? config["Interbase:DefaultDeviceId"] ?? Environment.MachineName;
    var id = await service.SaveRecordAsync(deviceId, req.CarNumber ?? "", lat, lon, screenshotBlob);
    return Results.Ok(new { id });
});

static string GetConnectionString(string? db, DbManager dbManager, IConfiguration config)
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

// --- API для просмотрщика БД ---
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
    var service = string.IsNullOrEmpty(db) ? defaultService : new NomeroffInterbaseService(connStr, logger);
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
    var service = string.IsNullOrEmpty(db) ? defaultService : new NomeroffInterbaseService(connStr, logger);
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
            if (testImage.Length > 1024 * 1024) // 1Mb
            {
                logger.LogWarning("test-write: 1.jpg слишком большой ({Size} байт), используем минимальный JPEG", testImage.Length);
                testImage = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAv/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBEQACEQD/ALH/2Q==");
            }
        }
        var id = await service.SaveRecordAsync("TEST_DEVICE", "A123BV777", 55.7558, 37.6173, testImage);
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
    var service = string.IsNullOrEmpty(db) ? defaultService : new NomeroffInterbaseService(connStr, logger);
    if (!service.IsConfigured)
        return Results.Ok(new { records = Array.Empty<object>(), message = "Выберите БД." });
    var l = limit ?? 100;
    var o = offset ?? 0;
    var records = await service.GetRecordsAsync(l, o);
    return Results.Ok(new { records });
});

app.MapDelete("/api/db/records/{id:long}", async (long id, string? db, NomeroffInterbaseService defaultService, DbManager dbManager, IConfiguration config, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("NomeroffInterbase");
    logger.LogInformation("api/db/records DELETE: id={Id}, db={Db}", id, db);
    var connStr = GetConnectionString(db, dbManager, config);
    var service = string.IsNullOrEmpty(db) ? defaultService : new NomeroffInterbaseService(connStr, logger);
    if (!service.IsConfigured)
        return Results.Problem("Выберите БД.");
    var ok = await service.DeleteRecordAsync(id);
    return ok ? Results.Ok(new { success = true }) : Results.Problem("Ошибка удаления.");
});

app.MapGet("/api/db/image/{id:long}", async (long id, string? db, NomeroffInterbaseService defaultService, DbManager dbManager, IConfiguration config, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("NomeroffInterbase");
    var connStr = GetConnectionString(db, dbManager, config);
    var service = string.IsNullOrEmpty(db) ? defaultService : new NomeroffInterbaseService(connStr, logger);
    if (!service.IsConfigured)
        return Results.NotFound();
    var bytes = await service.GetImageAsync(id);
    if (bytes == null || bytes.Length == 0)
        return Results.NotFound();
    return Results.File(bytes, "image/jpeg");
});

// --- Страницы ---
app.MapGet("/", () =>
{
    var html = new StringBuilder();
    html.AppendLine("<html><head><meta charset='utf-8'><title>Nomeroff Interbase API</title></head><body>");
    html.AppendLine("<h2>Nomeroff.Interbase.Api</h2><ul>");
    html.AppendLine("<li><a href='/db'>Просмотрщик БД</a></li>");
    html.AppendLine("<li><a href='/swagger'>Swagger</a></li>");
    html.AppendLine("</ul></body></html>");
    return Results.Content(html.ToString(), "text/html; charset=utf-8");
});

app.MapGet("/db", (HttpContext ctx, IWebHostEnvironment env) =>
{
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var html = Nomeroff.Interbase.Api.DbViewerHtml.GetHtml(baseUrl, env.WebRootPath ?? env.ContentRootPath);
    return Results.Content(html, "text/html; charset=utf-8");
});

app.Run();

public class RecordRequest
{
    public string? Db { get; set; }
    public string? ScreenshotBase64 { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? TimeUtc { get; set; }
    public string? CarNumber { get; set; }
    public string? DeviceId { get; set; }
}

public class CreateDbRequest
{
    public string? Name { get; set; }
}
