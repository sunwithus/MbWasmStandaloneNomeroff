using Microsoft.AspNetCore.Http.Features;
using System.Reflection;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var listenAddress = builder.Configuration["ListenAddress"] ?? "0.0.0.0";
var port = builder.Configuration.GetValue<int>("Port", 5553);
builder.WebHost.UseUrls($"http://{listenAddress}:{port}");

// Лимит размера загружаемого файла (по умолчанию ~30 MB; для видео — до 500 MB)
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long>("MaxVideoUploadBytes", 1024L * 1024 * 1024);
});

// Лимит размера multipart/form-data (загрузка видео через IFormFile)
builder.Services.Configure<FormOptions>(options =>
{
    // 1024 МБ (можно больше, если нужно)
    options.MultipartBodyLengthLimit = builder.Configuration.GetValue<long>(
        "MaxVideoUploadBytes",
        1024L * 1024 * 1024
    );
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Nomeroff.Video.Api", Version = "v1" });
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(pol =>
    {
        pol.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

builder.Services.AddHttpClient("Nomeroff", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["NomeroffApiBaseUrl"] ?? "http://127.0.0.1:8000";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(5);
});

var app = builder.Build();

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Nomeroff.Video.Api v1");
});

app.MapGet("/", () => Results.Content(
    "<html><head><meta charset='utf-8'><title>Nomeroff.Video.Api</title></head><body style='color: #ccc;'>" +
    "<h2>Nomeroff.Video.Api</h2><p>FFmpeg + Python API для обработки видео.</p>" +
    "<ul><li><a href='/swagger' style='color: lightblue;'>Swagger</a></li><li>POST /api/process-video</li></ul></body></html>",
    "text/html; charset=utf-8"));

app.MapPost("/api/process-video", async (IFormFile? file, int intervalSec, IConfiguration config, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest("Файл не передан");
    intervalSec = Math.Clamp(intervalSec, 1, 60);
    var pythonBase = config["NomeroffApiBaseUrl"] ?? "http://127.0.0.1:8000";
    logger.LogInformation("process-video: start, file={FileName}, size={SizeBytes} bytes, intervalSec={IntervalSec}, pythonBase={PythonBase}",
        file.FileName, file.Length, intervalSec, pythonBase);

    var tempDir = Path.Combine(Path.GetTempPath(), "nomeroff_video_" + Guid.NewGuid().ToString("N"));
    var ext = Path.GetExtension(file.FileName);
    if (string.IsNullOrEmpty(ext)) ext = ".mp4";
    var videoPath = Path.Combine(tempDir, "video" + ext);
    try
    {
        Directory.CreateDirectory(tempDir);
        await using (var fs = File.Create(videoPath))
            await file.CopyToAsync(fs, ct);
        logger.LogInformation("process-video: saved to {VideoPath}", videoPath);

        var framesDir = Path.Combine(tempDir, "frames");
        Directory.CreateDirectory(framesDir);
        var framePattern = Path.Combine(framesDir, "frame_%04d.jpg");
        //var ffmpeg = Environment.OSVersion.Platform == PlatformID.Win32NT ? "ffmpeg.exe" : "ffmpeg";
        // Директория проекта
        string assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string ffmpegPath = Path.GetFullPath(Path.Combine(assemblyLocation, "ffmpeg.exe"));
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpegPath,
            ArgumentList = { "-y", "-i", videoPath, "-vf", $"fps=1/{intervalSec}", "-q:v", "2", framePattern },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using (var proc = System.Diagnostics.Process.Start(psi))
        {
            if (proc == null)
            {
                logger.LogError("process-video: failed to start {Ffmpeg}", ffmpegPath);
                return Results.Problem("Не удалось запустить ffmpeg. Проверьте путь к ffmpeg");
            }
            logger.LogInformation("process-video: ffmpeg started, pid={Pid}", proc.Id);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                logger.LogError("process-video: ffmpeg exit code {Code}. Stderr: {Stderr}", proc.ExitCode, stderr);
                return Results.Problem("ffmpeg завершился с ошибкой. Проверьте формат видео.");
            }
        }

        var frameFiles = Directory.GetFiles(framesDir, "frame_*.jpg").OrderBy(f => f).ToList();
        var maxFrames = config.GetValue<int?>("MaxVideoFrames") ?? 300;
        if (frameFiles.Count > maxFrames)
        {
            frameFiles = frameFiles.Take(maxFrames).ToList();
            logger.LogInformation("process-video: limited to {Max} frames", maxFrames);
        }
        logger.LogInformation("process-video: {Count} frames to process", frameFiles.Count);

        var http = httpFactory.CreateClient("Nomeroff");
        var results = new List<object>();
        int frameIndex = 0;
        foreach (var framePath in frameFiles)
        {
            var bytes = await File.ReadAllBytesAsync(framePath, ct);
            var base64 = Convert.ToBase64String(bytes);
            var body = new { image_base64 = base64 };
            var response = await http.PostAsJsonAsync("api/process_frame", body, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("process-video: Python API returned {Code} for frame {Index}", response.StatusCode, frameIndex);
                frameIndex++;
                continue;
            }
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var timeSec = frameIndex * intervalSec;
            var plates = new List<string>();
            if (json.TryGetProperty("plates", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("plate", out var plate))
                    {
                        var s = plate.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) plates.Add(s);
                    }
                }
            }
            results.Add(new { timeSec, plates, imageBase64 = base64 });
            logger.LogDebug("process-video: frame {Index} @ {TimeSec}s, plates={Plates}", frameIndex, timeSec, plates.Count);
            frameIndex++;
        }
        logger.LogInformation("process-video: done, processed={Count}", frameIndex);
        return Results.Ok(new { totalFrames = results.Count, intervalSec, results });
    }
    finally
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
    }
})
.DisableAntiforgery();

app.Run();
