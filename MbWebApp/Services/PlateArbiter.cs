using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MbWebApp.Services;

/// <summary>
/// Второй каскад для треков, где голосование не сошлось: локальный VLM на
/// стороне Python выбирает номер из кандидатов трека.
///
/// Выключен по умолчанию. Основным проходом такую модель ставить нельзя —
/// она делит VRAM с nomeroff и склонна выдумывать правдоподобные номера,
/// поэтому вызывается только на спорных треках (обычно 5-15%), а ответ вне
/// формата РФ отбрасывается уже на стороне Python.
/// </summary>
public sealed class PlateArbiter
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<PlateArbiter> _logger;

    public PlateArbiter(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<PlateArbiter> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public bool IsEnabled => _config.GetValue("VlmArbiter:Enabled", false);

    /// <summary>Треки с уверенностью голосования ниже порога уходят арбитру.</summary>
    public double MinConfidence =>
        Math.Clamp(_config.GetValue("VlmArbiter:MinConfidence", 0.80), 0.0, 1.0);

    private sealed class ArbitrateResponse
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("plate")] public string Plate { get; set; } = "";
        [JsonPropertyName("decided_by")] public string DecidedBy { get; set; } = "";
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    /// <summary>
    /// Вернуть номер по мнению арбитра или null, если он выключен, недоступен
    /// или не дал валидного ответа — тогда остаётся результат голосования.
    /// </summary>
    public async Task<string?> TryArbitrateAsync(
        string? plateImageBase64,
        IReadOnlyCollection<string> candidates,
        CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrEmpty(plateImageBase64))
            return null;

        try
        {
            var http = _httpFactory.CreateClient("Nomeroff");
            var response = await http.PostAsJsonAsync(
                "api/arbitrate_plate",
                new { plate_image_base64 = plateImageBase64, candidates },
                ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("arbitrate_plate HTTP {Status}", response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<ArbitrateResponse>(cancellationToken: ct);
            if (body is null || !body.Enabled || string.IsNullOrEmpty(body.Plate))
                return null;

            _logger.LogInformation(
                "VLM-арбитр: {Plate} ({Source}) из кандидатов {Candidates}",
                body.Plate, body.DecidedBy, string.Join(", ", candidates));
            return body.Plate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "VLM-арбитр недоступен — остаёмся с голосованием");
            return null;
        }
    }
}
