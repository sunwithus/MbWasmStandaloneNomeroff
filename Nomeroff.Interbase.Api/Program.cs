using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Nomeroff.Interbase.Api.Interbase;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long>("MaxVideoUploadBytes", 1024L * 1024 * 1024);
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = builder.Configuration.GetValue<long>("MaxVideoUploadBytes", 1024L * 1024 * 1024);
});

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

var ibConnStr = builder.Configuration["Interbase:ConnectionString"] ?? "";
builder.Services.AddSingleton(new NomeroffInterbaseService(ibConnStr));

var app = builder.Build();

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Nomeroff.Interbase.Api v1");
});

// POST /api/records — сохранить запись в Interbase (URL GPS API — в заголовке X-Gps-Api-Base-Url от клиента)
app.MapPost("/api/records", async (HttpContext ctx, [FromBody] RecordRequest req, IConfiguration config, NomeroffInterbaseService ibService) =>
{
    if (!ibService.IsConfigured)
        return Results.Problem("Interbase не настроен. Укажите Interbase:ConnectionString в appsettings.json.");

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
    var id = await ibService.SaveRecordAsync(deviceId, req.Plate ?? "", lat, lon, screenshotBlob);
    return Results.Ok(new { id });
});

app.MapGet("/", () =>
{
    var html = new System.Text.StringBuilder();
    html.AppendLine("<html><head><meta charset='utf-8'><title>Nomeroff Interbase API</title></head><body>");
    html.AppendLine("<h2>Nomeroff.Interbase.Api</h2><ul>");
    html.AppendLine("<li><a href='/swagger'>Swagger UI</a></li>");
    html.AppendLine("<li>POST /api/records — сохранить запись в Interbase</li>");
    html.AppendLine("<li>POST /api/process-video — загрузить видео, ffmpeg → кадры → Python API</li>");
    html.AppendLine("<li>GET /api/device-name — имя ПК</li>");
    html.AppendLine("<li>GET /api/db/test — тест Interbase</li>");
    html.AppendLine("<li><a href='/db'>Просмотрщик БД</a> — таблица записей</li>");
    html.AppendLine("<p>URL Python API и GPS передаются клиентом (MbWasmStandaloneNomeroff) в заголовках.</p>");
    html.AppendLine("</ul></body></html>");
    return Results.Content(html.ToString(), "text/html; charset=utf-8");
});

app.MapGet("/api/db/test", async (NomeroffInterbaseService ibService) =>
{
    if (!ibService.IsConfigured)
        return Results.Ok(new { success = false, message = "Interbase не настроен." });
    var (success, message) = await ibService.TestConnectionAsync();
    return Results.Ok(new { success, message });
});

app.MapGet("/api/device-name", () => Results.Ok(new { name = Environment.MachineName }));

// Просмотрщик БД
app.MapGet("/api/db/records", async (int? limit, int? offset, NomeroffInterbaseService ibService) =>
{
    if (!ibService.IsConfigured)
        return Results.Ok(new { records = Array.Empty<object>(), message = "Interbase не настроен." });
    var l = limit ?? 100;
    var o = offset ?? 0;
    var records = await ibService.GetRecordsAsync(l, o);
    return Results.Ok(new { records });
});

app.MapGet("/db", async (NomeroffInterbaseService ibService, HttpContext ctx) =>
{
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var html = $@"
<!DOCTYPE html>
<html><head><meta charset='utf-8'><title>Просмотрщик БД</title>
<style>
  body {{ font-family: sans-serif; margin: 20px; background: #1e1e1e; color: #ddd; }}
  table {{ border-collapse: collapse; width: 100%; }}
  th, td {{ border: 1px solid #444; padding: 8px; text-align: left; }}
  th {{ background: #333; }}
  tr:nth-child(even) {{ background: #252525; }}
  .nav {{ margin: 10px 0; }}
  .nav button, .nav span {{ margin-right: 10px; padding: 6px 12px; }}
  .error {{ color: #f88; }}
</style></head><body>
<h2>Просмотрщик БД Interbase</h2>
<div class='nav'>
  <button onclick='load(0)'>Обновить</button>
  <span id='info'></span>
</div>
<table><thead><tr>
  <th>S_INCKEY</th><th>S_DEVICEID</th><th>S_DATETIME</th><th>S_NOTICE</th><th>Широта</th><th>Долгота</th>
</tr></thead><tbody id='tbody'></tbody></table>
<script>
  const base = '{baseUrl}';
  async function load(offset) {{
    document.getElementById('tbody').innerHTML = '<tr><td colspan=6>Загрузка...</td></tr>';
    try {{
      const r = await fetch(base + '/api/db/records?limit=100&offset=' + offset);
      const j = await r.json();
      if (j.message) {{
        document.getElementById('tbody').innerHTML = '<tr><td colspan=6 class=error>' + j.message + '</td></tr>';
        return;
      }}
      let html = '';
      for (const row of j.records || []) {{
        html += '<tr><td>' + row.s_INCKEY + '</td><td>' + (row.s_DEVICEID || '') + '</td><td>' +
          (row.s_DATETIME ? new Date(row.s_DATETIME).toLocaleString() : '') + '</td><td>' +
          (row.s_NOTICE || '') + '</td><td>' + (row.s_LATITUDE ?? '') + '</td><td>' + (row.s_LONGITUDE ?? '') + '</td></tr>';
      }}
      document.getElementById('tbody').innerHTML = html || '<tr><td colspan=6>Нет записей</td></tr>';
      document.getElementById('info').textContent = 'Загружено: ' + (j.records?.length || 0);
    }} catch (e) {{
      document.getElementById('tbody').innerHTML = '<tr><td colspan=6 class=error>Ошибка: ' + e.message + '</td></tr>';
    }}
  }}
  load(0);
</script></body></html>";
    return Results.Content(html, "text/html; charset=utf-8");
});

// Обработка видео (URL Python API — в заголовке X-Nomeroff-Api-Base-Url от клиента)
app.MapPost("/api/process-video", async (HttpContext ctx, IFormFile? file, int intervalSec, IConfiguration config, ILogger<Program> logger, CancellationToken ct) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest("Файл не передан");
    intervalSec = Math.Clamp(intervalSec, 1, 60);
    var pythonBase = ctx.Request.Headers["X-Nomeroff-Api-Base-Url"].FirstOrDefault()?.TrimEnd('/')
        ?? "http://127.0.0.1:8000";
    logger.LogInformation("process-video: start, file={FileName}, size={SizeBytes}, intervalSec={IntervalSec}",
        file.FileName, file.Length, intervalSec);
    var tempDir = Path.Combine(Path.GetTempPath(), "nomeroff_video_" + Guid.NewGuid().ToString("N"));
    var videoPath = Path.Combine(tempDir, "video" + Path.GetExtension(file.FileName));
    try
    {
        Directory.CreateDirectory(tempDir);
        await using (var fs = File.Create(videoPath))
            await file.CopyToAsync(fs, ct);
        var framesDir = Path.Combine(tempDir, "frames");
        Directory.CreateDirectory(framesDir);
        var framePattern = Path.Combine(framesDir, "frame_%04d.jpg");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg.exe",
            ArgumentList = { "-y", "-i", videoPath, "-vf", $"fps=1/{intervalSec}", "-q:v", "2", framePattern },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using (var p = System.Diagnostics.Process.Start(psi))
        {
            if (p == null)
            {
                logger.LogError("process-video: failed to start ffmpeg.exe");
                return Results.Problem("Не удалось запустить ffmpeg. Установите ffmpeg и добавьте в PATH.");
            }
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
            {
                var stderr = await p.StandardError.ReadToEndAsync(ct);
                logger.LogError("process-video: ffmpeg exited {ExitCode}. {Stderr}", p.ExitCode, stderr);
                return Results.Problem("ffmpeg завершился с ошибкой.");
            }
        }
        var frameFiles = Directory.GetFiles(framesDir, "frame_*.jpg").OrderBy(f => f).ToList();
        var maxFrames = config.GetValue<int?>("MaxVideoFrames") ?? 300;
        if (frameFiles.Count > maxFrames)
            frameFiles = frameFiles.Take(maxFrames).ToList();
        using var http = new HttpClient { BaseAddress = new Uri(pythonBase + "/"), Timeout = TimeSpan.FromMinutes(2) };
        var results = new List<object>();
        int frameIndex = 0;
        foreach (var framePath in frameFiles)
        {
            var bytes = await File.ReadAllBytesAsync(framePath, ct);
            var base64 = Convert.ToBase64String(bytes);
            var body = new { image_base64 = base64 };
            var response = await http.PostAsJsonAsync("api/process_frame", body, ct);
            if (!response.IsSuccessStatusCode) continue;
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var timeSec = frameIndex * intervalSec;
            var plates = new List<string>();
            if (json.TryGetProperty("plates", out var arr))
                foreach (var item in arr.EnumerateArray())
                    if (item.TryGetProperty("plate", out var plate))
                        plates.Add(plate.GetString() ?? "");
            results.Add(new { timeSec, plates });
            frameIndex++;
        }
        return Results.Ok(new { totalFrames = results.Count, intervalSec, results });
    }
    finally
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
    }
})
.DisableAntiforgery();

app.Run();

public class RecordRequest
{
    public string? ScreenshotBase64 { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? TimeUtc { get; set; }
    public string? Plate { get; set; }
    public string? DeviceId { get; set; }
    public string? Reserved1 { get; set; }
    public string? Reserved2 { get; set; }
    public string? Reserved3 { get; set; }
}
