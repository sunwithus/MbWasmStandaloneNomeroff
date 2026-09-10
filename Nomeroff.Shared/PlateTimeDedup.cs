namespace Nomeroff.Shared;

/// <summary>
/// Дедуп записи в БД по времени внутри одного ролика (и камеры).
/// Тот же ствол номера в пределах интервала не пишется повторно.
/// 0 = выключено. Окно скользящее: каждое появление сдвигает точку отсчёта,
/// поэтому машина в кадре 40 секунд даёт одну запись, а не по одной каждые N с.
/// </summary>
public static class PlateTimeDedup
{
    public const int DefaultIntervalSec = 10;

    /// <summary>
    /// Проверить дубль и запомнить это появление. Вызывать в порядке возрастания времени.
    /// </summary>
    public static bool IsDuplicateAndTouch(
        IDictionary<string, double> lastSecByStem,
        string plate,
        double timeSec,
        int intervalSec)
    {
        var stem = PlateAlphabet.DedupStem(plate);
        if (string.IsNullOrEmpty(stem))
            return false;

        var dup = intervalSec > 0
                  && lastSecByStem.TryGetValue(stem, out var prev)
                  && Math.Abs(timeSec - prev) < intervalSec;
        lastSecByStem[stem] = timeSec;
        return dup;
    }
}
