using System.Text;
using MbWebApp.Components;
using MbWebApp.Services;
using Microsoft.AspNetCore.Http.Features;
using MudBlazor.Services;
using Nomeroff.Gps.Api;
using Nomeroff.Interbase.Api;
using Nomeroff.Video.Api;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

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
    o.PlateMinConfidence = builder.Configuration.GetValue("PlateMinConfidence", 0.70);
    o.NomeroffApiBaseUrl = builder.Configuration["NomeroffApiBaseUrl"] ?? "http://127.0.0.1:8000";
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

var urls = builder.Configuration["Urls"]
           ?? builder.Configuration["Kestrel:Endpoints:Http:Url"]
           ?? "http://0.0.0.0:5555";
builder.WebHost.UseUrls(urls);

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

app.MapGet("/health", (IConfiguration config) => Results.Ok(new
{
    status = "ok",
    modules = new[] { "ui", "gps", "interbase", "video" },
    pythonHint = config["NomeroffApiBaseUrl"] ?? "http://127.0.0.1:8000",
    maxVideoFrames = config.GetValue("MaxVideoFrames", 300),
    plateMinConfidence = config.GetValue("PlateMinConfidence", 0.70)
}));

app.MapNomeroffGpsEndpoints("/ops/gps");
app.MapNomeroffInterbaseEndpoints();
app.MapNomeroffVideoEndpoints();
app.MapFolderWatchEndpoints();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
