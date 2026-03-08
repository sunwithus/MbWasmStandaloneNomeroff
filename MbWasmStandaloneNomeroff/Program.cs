using MbWasmStandaloneNomeroff;
using MbWasmStandaloneNomeroff.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<NomeroffService>();
builder.Services.AddScoped<RecordsService>();

await builder.Build().RunAsync();
