using System.Text;
using MbWebApp.Components;
using MbWebApp.Options;
using MbWebApp.Services;
using MbWebApp;
using Microsoft.AspNetCore.Http.Features;
using MudBlazor.Services;
using Nomeroff.Gps.Api;
using Nomeroff.Interbase.Api;
using Nomeroff.Video.Api;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["NomeroffApiBaseUrl"] = AppPorts.OcrBaseUrl(builder.Configuration),
    ["Urls"] = AppPorts.AppListenUrl(builder.Configuration),
    ["AppBaseUrl"] = AppPorts.AppBaseUrl(builder.Configuration)
});

builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("MbWebApp", LogLevel.Debug);
var logBuffer = new LogBufferService();
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new FileLoggerProvider(logBuffer));

builder.Services.AddMudServices();
builder.Services.AddScoped(sp => new HttpClient());
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<NomeroffService>();
builder.Services.AddScoped<RecordsService>();
builder.Services.AddScoped<PlateArbiter>();
builder.Services.AddScoped<VideoResultProcessor>();
builder.Services.AddScoped<RecognitionStateService>();
builder.Services.AddSingleton<FolderDiskQueue>();
builder.Services.AddSingleton<FolderWatchState>();
builder.Services.AddSingleton<FolderWatchService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FolderWatchService>());

// Единый конфиг: appsettings + environment variables (MaxVideoFrames, PlateMinConfidence, FolderWatch__*, …)
builder.Services.Configure<MbWebApp.Options.NomeroffAppOptions>(o =>
{
    o.MaxVideoFrames = builder.Configuration.GetValue("MaxVideoFrames", 300);
    o.PlateMinConfidence = builder.Configuration.GetValue("PlateMinConfidence", 0.60);
    o.NomeroffApiBaseUrl = AppPorts.OcrBaseUrl(builder.Configuration);
    o.MaxVideoUploadBytes = builder.Configuration.GetValue("MaxVideoUploadBytes", 1024L * 1024 * 1024);
});
builder.Services.Configure<MbWebApp.Options.FolderWatchOptions>(
    builder.Configuration.GetSection(MbWebApp.Options.FolderWatchOptions.Section));

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 512 * 1024 * 1024;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "MbWebApp (UI + GPS + Interbase + Video)", Version = "v1" });
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(pol => pol.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// Модули бывших отдельных API (один процесс)
builder.Services.AddNomeroffGps();
builder.Services.AddNomeroffInterbase(builder.Configuration, AppContext.BaseDirectory);
builder.Services.AddNomeroffVideo(builder.Configuration);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long>("MaxVideoUploadBytes", 1024L * 1024 * 1024);
});

builder.WebHost.UseUrls(AppPorts.AppListenUrl(builder.Configuration));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseCors();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "MbWebApp API");
});

app.MapAppHealth();

// Полный конвейер без записи в БД: кадры -> треки -> голосование -> итоговые
// номера. Нужен регрессионному стенду и разбору жалоб «почему этого номера нет»:
// сырые чтения из /api/process-video-path показывают работу распознавателя, а
// сюда попадает уже то, что ушло бы в БД.
app.MapPost("/api/analyze-video-path", async (
    AnalyzeVideoRequest body,
    RecordsService records,
    VideoResultProcessor processor,
    IConfiguration config,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Path))
        return Results.BadRequest("path не передан");

    var path = Path.GetFullPath(body.Path.Trim());
    if (!File.Exists(path))
        return Results.NotFound($"Файл не найден: {path}");

    var sampleFps = body.SampleFps ?? config.GetValue("SampleFps", 3.0);
    var started = DateTime.UtcNow;
    var api = await records.ProcessVideoFromPathAsync(path, sampleFps, null, ct);
    if (api == null)
        return Results.Problem("Конвейер не вернул результат");

    var outcome = await processor.ProcessAsync(new VideoResultProcessRequest
    {
        ApiResponse = api,
        SaveToDb = false,
        MinFrameHits = body.MinFrameHits ?? config.GetValue("PlateMinFrameHits", 2),
        Source = "benchmark"
    }, ct);

    return Results.Ok(new
    {
        elapsedSec = (DateTime.UtcNow - started).TotalSeconds,
        sampleFps,
        summary = outcome.Summary,
        // plates — после голосования (одна запись на трек), results — сырые чтения
        plates = outcome.Tracks,
        totalFrames = api.TotalFrames,
        results = api.Results.Select(fr => new
        {
            timeSec = fr.TimeSec,
            overlayTimeUtc = fr.OverlayTimeUtc,
            latitude = fr.Latitude,
            longitude = fr.Longitude,
            plates = fr.Plates.Select(p => new
            {
                plate = p.Plate,
                confidence = p.Confidence,
                ocrConfidence = p.OcrConfidence
            })
        })
    });
}).DisableAntiforgery();

app.MapNomeroffGpsEndpoints("/ops/gps");
app.MapNomeroffInterbaseEndpoints();
app.MapNomeroffVideoEndpoints();
app.MapFolderWatchEndpoints();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Вход /api/analyze-video-path: путь к ролику и опциональные пороги.</summary>
public sealed class AnalyzeVideoRequest
{
    public string Path { get; set; } = "";
    public double? SampleFps { get; set; }
    public int? MinFrameHits { get; set; }
}
