namespace Nomeroff.Shared;

/// <summary>Одно чтение номера с одного кадра.</summary>
public sealed class PlateReading
{
    public required string Plate { get; init; }
    /// <summary>Вероятности по символам из CTC-головы; пусто — голос с нейтральным весом.</summary>
    public IReadOnlyList<double> CharProbs { get; init; } = Array.Empty<double>();
    /// <summary>Уверенность OCR чтения в целом (минимум по символам).</summary>
    public double OcrConfidence { get; init; }
    public double TimeSec { get; init; }
}

public sealed class PlateVoteResult
{
    public required string Plate { get; init; }
    /// <summary>Доля веса, набранная победившими глифами (0..1).</summary>
    public double Confidence { get; init; }
    /// <summary>В скольких разных кадрах номер был встречен.</summary>
    public int FrameHits { get; init; }
    /// <summary>
    /// В скольких разных кадрах прочитан ровно этот текст.
    ///
    /// Отличать от FrameHits важно для мусора: табличка «АВАРИЙНАЯ» на борту
    /// даёт по чтению в каждом кадре, но каждый раз другое (А835НМ69, А840НМ69,
    /// А849НМ69), тогда как настоящий номер повторяется символ в символ.
    /// </summary>
    public int AgreeingFrameHits { get; init; }
}

/// <summary>
/// Межкадровое голосование по позициям символов.
///
/// Верный ответ обычно уже есть в сырых чтениях: В713ВВ125 читается 3 раза,
/// а В713НВ129 — один. Раньше побеждало чтение с максимальным score детектора,
/// то есть ровно наоборот. Здесь на каждой позиции берётся мажоритарный глиф
/// с весом по уверенности OCR.
/// </summary>
public static class PlateVote
{
    private const double NeutralWeight = 0.5;

    /// <summary>
    /// Проголосовать по группе чтений одного номера.
    /// Возвращает null, если чтений нет или все пустые.
    /// </summary>
    public static PlateVoteResult? Vote(IEnumerable<PlateReading> readings)
    {
        var usable = readings
            .Select(r => (Plate: PlateAlphabet.Normalize(r.Plate), r.CharProbs, r.TimeSec))
            .Where(r => r.Plate.Length > 0)
            .ToList();
        if (usable.Count == 0)
            return null;

        // Голосуем внутри самой популярной длины: смешивать А123ВС45 и А123ВС125
        // по позициям нельзя — «сдвинется» регион. При равном числе голосов
        // предпочитаем более длинный вариант (полный трёхзначный регион).
        var group = usable
            .GroupBy(r => r.Plate.Length)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key)
            .First()
            .ToList();

        var length = group[0].Plate.Length;
        var chars = new char[length];
        var confidences = new double[length];

        for (var pos = 0; pos < length; pos++)
        {
            var weights = new Dictionary<char, double>();
            var total = 0.0;
            foreach (var (plate, probs, _) in group)
            {
                var ch = plate[pos];
                var w = pos < probs.Count ? probs[pos] : NeutralWeight;
                weights[ch] = weights.GetValueOrDefault(ch) + w;
                total += w;
            }
            var winner = weights.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First();
            chars[pos] = winner.Key;
            confidences[pos] = total > 0 ? winner.Value / total : 0.0;
        }

        var frameHits = group.Select(r => Math.Round(r.TimeSec, 2)).Distinct().Count();
        var voted = new string(chars);
        var agreeing = group
            .Where(r => string.Equals(r.Plate, voted, StringComparison.Ordinal))
            .Select(r => Math.Round(r.TimeSec, 2))
            .Distinct()
            .Count();
        return new PlateVoteResult
        {
            Plate = voted,
            Confidence = confidences.Average(),
            FrameHits = frameHits,
            AgreeingFrameHits = agreeing
        };
    }
}
