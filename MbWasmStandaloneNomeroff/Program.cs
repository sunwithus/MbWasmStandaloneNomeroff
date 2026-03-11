using MbWasmStandaloneNomeroff;
using MbWasmStandaloneNomeroff.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("MbWasmStandaloneNomeroff", LogLevel.Debug);
var logBuffer = new LogBufferService();
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new FileLoggerProvider(logBuffer));
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<NomeroffService>();
builder.Services.AddScoped<RecordsService>();
builder.Services.AddSingleton<RecognitionStateService>();

await builder.Build().RunAsync();
