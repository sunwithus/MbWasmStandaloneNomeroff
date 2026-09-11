using System.Text.Json;
using System.Text.Json.Serialization;
using MbWebApp.Options;

namespace MbWebApp;

/// <summary>
/// /health приложения: модули UI + живой снимок OCR (CUDA/CPU, имя GPU, VRAM).
/// OCR недоступен — приложение всё равно отвечает ok, в блоке ocr.reachable=false.
/// </summary>
public static class HealthEndpointExtensions
{
    private static readonly JsonSerializerOptions OcrJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static WebApplication MapAppHealth(this WebApplication app)
    {
        app.MapGet("/health", async (
            IConfiguration config,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            var ocr = await ProbeOcrAsync(httpFactory, ct);
            var inference = ocr.Reachable ? (ocr.Device ?? "unknown") : "ocr_unreachable";
            return Results.Ok(new
            {
                status = "ok",
                modules = new[] { "ui", "gps", "interbase", "video" },
                pythonHint = AppPorts.OcrBaseUrl(config),
                maxVideoFrames = config.GetValue("MaxVideoFrames", 300),
                plateMinConfidence = config.GetValue("PlateMinConfidence", 0.60),
                ffmpegHwAccel = config.GetValue("FfmpegHwAccel", true),
                compute = new
                {
                    inference,
                    inferenceDevice = ocr.DeviceName,
                    preprocess = "cpu",
                    videoDecode = config.GetValue("FfmpegHwAccel", true)
                        ? "ffmpeg-hwaccel-auto"
                        : "cpu"
                },
                gpu = new
                {
                    available = ocr.GpuAvailable,
                    used = string.Equals(ocr.Device, "cuda", StringComparison.OrdinalIgnoreCase),
                    name = ocr.DeviceName,
                    vramUsedMb = ocr.VramUsedMb,
                    vramTotalMb = ocr.VramTotalMb,
                    vramAllocatedMb = ocr.VramAllocatedMb,
                    vramReservedMb = ocr.VramReservedMb
                },
                ocr = new
                {
                    reachable = ocr.Reachable,
                    status = ocr.Status,
                    modelLoaded = ocr.ModelLoaded,
                    device = ocr.Device,
                    deviceName = ocr.DeviceName,
                    devicePolicy = ocr.DevicePolicy,
                    yoloBatch = ocr.YoloBatch
                }
            });
        });
        return app;
    }

    private static async Task<OcrProbe> ProbeOcrAsync(IHttpClientFactory httpFactory, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient("Nomeroff");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(2));
            using var resp = await client.GetAsync("health", linked.Token);
            if (!resp.IsSuccessStatusCode)
                return new OcrProbe { Reachable = false };

            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
            var dto = await JsonSerializer.DeserializeAsync<OcrHealthDto>(stream, OcrJson, linked.Token);
            if (dto == null)
                return new OcrProbe { Reachable = true, Status = "ok" };

            return new OcrProbe
            {
                Reachable = true,
                Status = dto.Status ?? "ok",
                ModelLoaded = dto.ModelLoaded,
                GpuAvailable = dto.GpuAvailable,
                Device = dto.Device,
                DeviceName = dto.DeviceName,
                DevicePolicy = dto.DevicePolicy,
                VramUsedMb = dto.VramUsedMb,
                VramTotalMb = dto.VramTotalMb,
                VramAllocatedMb = dto.VramAllocatedMb,
                VramReservedMb = dto.VramReservedMb,
                YoloBatch = dto.YoloBatch
            };
        }
        catch
        {
            return new OcrProbe { Reachable = false };
        }
    }

    private sealed class OcrHealthDto
    {
        public string? Status { get; set; }
        public bool ModelLoaded { get; set; }
        public bool GpuAvailable { get; set; }
        public string? Device { get; set; }
        public string? DeviceName { get; set; }
        public string? DevicePolicy { get; set; }
        public double? VramUsedMb { get; set; }
        public double? VramTotalMb { get; set; }
        public double? VramAllocatedMb { get; set; }
        public double? VramReservedMb { get; set; }
        public int? YoloBatch { get; set; }
    }

    private sealed class OcrProbe
    {
        public bool Reachable { get; init; }
        public string? Status { get; init; }
        public bool ModelLoaded { get; init; }
        public bool GpuAvailable { get; init; }
        public string? Device { get; init; }
        public string? DeviceName { get; init; }
        public string? DevicePolicy { get; init; }
        public double? VramUsedMb { get; init; }
        public double? VramTotalMb { get; init; }
        public double? VramAllocatedMb { get; init; }
        public double? VramReservedMb { get; init; }
        public int? YoloBatch { get; init; }
    }
}
