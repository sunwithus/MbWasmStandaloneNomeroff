using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace Nomeroff.Video.Api;

public static class VideoServiceCollectionExtensions
{
    public static IServiceCollection AddNomeroffVideo(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<GpsOcrOptions>(config.GetSection(GpsOcrOptions.SectionName));
        services.AddSingleton<GpsOverlayOcr>();
        services.AddSingleton<VideoFileProcessor>();
        services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = config.GetValue<long>("MaxVideoUploadBytes", 1024L * 1024 * 1024);
        });
        services.AddHttpClient("Nomeroff", (sp, client) =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var baseUrl = cfg["NomeroffApiBaseUrl"] ?? "http://127.0.0.1:8000";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        return services;
    }
}

public static class VideoEndpointExtensions
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static WebApplication MapNomeroffVideoEndpoints(this WebApplication app)
    {
        app.MapGet("/ops/video", () => Results.Content(
            "<html><head><meta charset='utf-8'><title>Nomeroff Video</title></head><body style='color: #ccc;'>" +
            "<h2>Video (в составе MbWebApp)</h2><p>FFmpeg + Python API для обработки видео.</p>" +
            "<ul><li><a href='/swagger' style='color: lightblue;'>Swagger</a></li>" +
            "<li>POST /api/process-video</li><li>POST /api/process-video-path</li></ul></body></html>",
            "text/html; charset=utf-8"));

        app.MapPost("/api/process-video", async (HttpRequest request, int? intervalSec, double? sampleFps, VideoFileProcessor processor, IConfiguration config, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffVideo");
            if (!request.HasFormContentType)
                return Results.BadRequest("Ожидается multipart/form-data");

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
                return Results.BadRequest("Файл не передан");

            var options = ResolveOptions(config, intervalSec, sampleFps);
            logger.LogInformation("process-video: upload start, file={FileName}, size={Size}, fps={Fps}",
                file.FileName, file.Length, options.SampleFps);

            var tempDir = Path.Combine(Path.GetTempPath(), "nomeroff_video_" + Guid.NewGuid().ToString("N"));
            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".mp4";
            // Имя сохраняем: из него берётся время начала записи (NO<дата>-<время>)
            var safeName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "video" + ext;
            var videoPath = Path.Combine(tempDir, safeName);
            Directory.CreateDirectory(tempDir);
            await using (var fs = File.Create(videoPath))
                await file.CopyToAsync(fs, ct);

            return NdjsonStream(async (emit, token) =>
            {
                try
                {
                    await processor.ProcessAsync(videoPath, options, emit, token);
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* ignore */ }
                }
            });
        }).DisableAntiforgery();

        app.MapPost("/api/process-video-path", (ProcessVideoPathRequest? body, int? intervalSec, double? sampleFps, VideoFileProcessor processor, IConfiguration config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffVideo");
            if (body == null || string.IsNullOrWhiteSpace(body.Path))
                return Results.BadRequest("Укажите path");

            var path = Path.GetFullPath(body.Path.Trim());
            if (!File.Exists(path))
                return Results.NotFound($"Файл не найден: {path}");

            var options = ResolveOptions(config, intervalSec, sampleFps);
            logger.LogInformation("process-video-path: {Path}, fps={Fps}", path, options.SampleFps);

            return NdjsonStream(async (emit, token) =>
            {
                await processor.ProcessAsync(path, options, emit, token);
            });
        }).DisableAntiforgery();

        return app;
    }

    /// <summary>
    /// sampleFps — основной вход. intervalSec остаётся ради старого UI и означает
    /// «один кадр в N секунд», то есть fps = 1/N.
    /// </summary>
    private static VideoProcessOptions ResolveOptions(IConfiguration config, int? intervalSec, double? sampleFps)
    {
        if (sampleFps is > 0)
            return new VideoProcessOptions { SampleFps = Math.Clamp(sampleFps.Value, 0.05, 30.0) };
        if (intervalSec is > 0)
            return VideoProcessOptions.FromIntervalSec(intervalSec.Value);
        return new VideoProcessOptions { SampleFps = config.GetValue("SampleFps", 3.0) };
    }

    private static IResult NdjsonStream(Func<Func<object, CancellationToken, Task>, CancellationToken, Task> run) =>
        Results.Stream(async stream =>
        {
            async Task Emit(object payload, CancellationToken token)
            {
                var line = JsonSerializer.Serialize(payload, JsonOpts) + "\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
            }

            try
            {
                await run(Emit, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // client disconnected
            }
            catch (Exception ex)
            {
                try { await Emit(new { type = "error", message = ex.Message }, CancellationToken.None); } catch { /* ignore */ }
            }
        }, contentType: "application/x-ndjson; charset=utf-8");
}

public sealed class ProcessVideoPathRequest
{
    public string Path { get; set; } = "";
}
