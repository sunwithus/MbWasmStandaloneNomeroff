using MbWebApp.Components;
using MbWebApp.Services;
using MudBlazor.Services;

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
builder.Services.AddScoped<RecognitionStateService>();

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 512 * 1024 * 1024; // MB — кадры камеры в base64
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
