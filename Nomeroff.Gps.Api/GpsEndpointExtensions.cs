namespace Nomeroff.Gps.Api;

public static class GpsServiceCollectionExtensions
{
    public static IServiceCollection AddNomeroffGps(this IServiceCollection services)
    {
        services.AddSingleton<IGpsService, NmeaGpsService>();
        return services;
    }
}

public static class GpsEndpointExtensions
{
    public static WebApplication MapNomeroffGpsEndpoints(this WebApplication app, string opsUiPath = "/ops/gps")
    {
        var indexPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "ops", "gps", "index.html");
        if (!File.Exists(indexPath))
            indexPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "index.html");

        app.MapGet(opsUiPath, () =>
        {
            if (File.Exists(indexPath))
                return Results.Content(File.ReadAllText(indexPath), "text/html; charset=utf-8");
            return Results.Content(
                "<html><body style='color:#ccc'><h2>GPS API</h2><p>/api/ports, /api/connect, /api/position</p></body></html>",
                "text/html; charset=utf-8");
        });

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
            // Всегда валидный JSON: пустой body ломает GetFromJsonAsync у клиентов.
            return Results.Json(new
            {
                latitude = p?.Latitude,
                longitude = p?.Longitude,
                hasPosition = p != null
            });
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

        return app;
    }
}

public class GpsConnectRequest
{
    public string? Port { get; set; }
}
