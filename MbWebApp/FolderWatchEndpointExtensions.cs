using System.Text.Json;
using MbWebApp.Services;

namespace MbWebApp.Services;

public static class FolderWatchEndpointExtensions
{
    public static WebApplication MapFolderWatchEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/folder-watch").DisableAntiforgery();

        g.MapGet("/config", (FolderWatchState state) => Results.Ok(state.GetConfig()));

        g.MapPost("/config", async (HttpRequest request, FolderWatchState state) =>
        {
            using var doc = await JsonDocument.ParseAsync(request.Body);
            var cfg = JsonSerializer.Deserialize<FolderWatchConfig>(doc.RootElement.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });
            if (cfg == null) return Results.BadRequest();
            state.SaveConfig(cfg);
            return Results.Ok(state.GetConfig());
        });

        g.MapGet("/status", (FolderWatchState state) => Results.Ok(state.Snapshot()));

        g.MapPost("/start", (FolderWatchService svc, FolderWatchState state) =>
        {
            svc.RequestStart();
            return Results.Ok(state.Snapshot());
        });

        g.MapPost("/stop", (FolderWatchService svc, FolderWatchState state) =>
        {
            svc.RequestStop();
            return Results.Ok(state.Snapshot());
        });

        g.MapPost("/scan", (FolderWatchService svc, FolderWatchState state) =>
        {
            svc.RequestScanOnce();
            return Results.Ok(state.Snapshot());
        });

        return app;
    }
}
