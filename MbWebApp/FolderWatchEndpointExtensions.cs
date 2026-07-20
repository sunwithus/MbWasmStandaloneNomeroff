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

        g.MapGet("/status", (FolderWatchState state, FolderDiskQueue diskQueue) =>
        {
            var snap = state.Snapshot();
            snap.DiskQueueCount = diskQueue.Count(snap.Config);
            return Results.Ok(snap);
        });

        g.MapPost("/start", (FolderWatchService svc, FolderWatchState state, FolderDiskQueue diskQueue) =>
        {
            svc.RequestStart();
            var snap = state.Snapshot();
            snap.DiskQueueCount = diskQueue.Count(snap.Config);
            return Results.Ok(snap);
        });

        g.MapPost("/stop", (FolderWatchService svc, FolderWatchState state, FolderDiskQueue diskQueue) =>
        {
            svc.RequestStop();
            var snap = state.Snapshot();
            snap.DiskQueueCount = diskQueue.Count(snap.Config);
            return Results.Ok(snap);
        });

        g.MapPost("/scan", (FolderWatchService svc, FolderWatchState state, FolderDiskQueue diskQueue) =>
        {
            svc.RequestScanOnce();
            var snap = state.Snapshot();
            snap.DiskQueueCount = diskQueue.Count(snap.Config);
            return Results.Ok(snap);
        });

        return app;
    }
}
