using Nomeroff.Gps.Api;

var builder = WebApplication.CreateBuilder(args);

var listenAddress = builder.Configuration["ListenAddress"] ?? "0.0.0.0";
var port = builder.Configuration.GetValue<int>("Port", 5551);
builder.WebHost.UseUrls($"http://{listenAddress}:{port}");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Nomeroff.Gps.Api", Version = "v1" });
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(pol =>
    {
        pol.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

builder.Services.AddSingleton<IGpsService, NmeaGpsService>();

var app = builder.Build();

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Nomeroff.Gps.Api v1");
});

var indexHtml = File.ReadAllText(Path.Combine(app.Environment.ContentRootPath, "wwwroot", "index.html"));

app.MapGet("/", () => Results.Content(indexHtml, "text/html; charset=utf-8"));

app.MapGet("/api/ports", async (IGpsService gps) =>
    Results.Ok(await gps.GetAvailablePortsAsync()));

app.MapPost("/api/connect", async (GpsConnectRequest req, IGpsService gps) =>
{
    var ok = await gps.ConnectAsync(req.Port ?? "");
    return Results.Ok(new { success = ok });
});

app.MapPost("/api/disconnect", async (IGpsService gps) =>
{
    await gps.DisconnectAsync();
    return Results.Ok(new { success = true });
});

app.MapGet("/api/position", (IGpsService gps) =>
{
    var p = gps.CurrentPosition;
    return Results.Ok(p != null ? new { latitude = p.Latitude, longitude = p.Longitude } : (object?)null);
});

app.MapGet("/api/test", (IGpsService gps) =>
{
    var p = gps.CurrentPosition;
    if (p != null)
        return Results.Ok(new { success = true, latitude = p.Latitude, longitude = p.Longitude });
    return Results.Ok(new { success = false, message = "GPS не подключен. POST /api/connect с портом." });
});

app.MapGet("/api/status", (IGpsService gps) =>
{
    var p = gps.CurrentPosition;
    return Results.Ok(new
    {
        connected = gps.IsConnected,
        hasPosition = p != null,
        latitude = p?.Latitude,
        longitude = p?.Longitude,
        timestamp = p?.Timestamp?.ToString("o")
    });
});

app.Run();

public class GpsConnectRequest
{
    public string? Port { get; set; }
}
