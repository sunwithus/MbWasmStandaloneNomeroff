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

        app.MapPost("/api/process-video", async (HttpRequest request, int intervalSec, VideoFileProcessor processor, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffVideo");
            if (!request.HasFormContentType)
                return Results.BadRequest("Ожидается multipart/form-data");

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
                return Results.BadRequest("Файл не передан");

            intervalSec = Math.Clamp(intervalSec, 1, 60);
            logger.LogInformation("process-video: upload start, file={FileName}, size={Size}, interval={Interval}",
                file.FileName, file.Length, intervalSec);

            var tempDir = Path.Combine(Path.GetTempPath(), "nomeroff_video_" + Guid.NewGuid().ToString("N"));
            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".mp4";
            var videoPath = Path.Combine(tempDir, "video" + ext);
            Directory.CreateDirectory(tempDir);
            await using (var fs = File.Create(videoPath))
                await file.CopyToAsync(fs, ct);

            return NdjsonStream(async (emit, token) =>
            {
                try
                {
                    await processor.ProcessAsync(videoPath, intervalSec, emit, token);
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* ignore */ }
                }
            });
        }).DisableAntiforgery();

        app.MapPost("/api/process-video-path", async (ProcessVideoPathRequest? body, int intervalSec, VideoFileProcessor processor, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("NomeroffVideo");
            if (body == null || string.IsNullOrWhiteSpace(body.Path))
                return Results.BadRequest("Укажите path");

            var path = Path.GetFullPath(body.Path.Trim());
            if (!File.Exists(path))
                return Results.NotFound($"Файл не найден: {path}");

            intervalSec = Math.Clamp(intervalSec <= 0 ? 1 : intervalSec, 1, 60);
            logger.LogInformation("process-video-path: {Path}, interval={Interval}", path, intervalSec);

            return NdjsonStream(async (emit, token) =>
            {
                await processor.ProcessAsync(path, intervalSec, emit, token);
            });
        }).DisableAntiforgery();

        return app;
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
