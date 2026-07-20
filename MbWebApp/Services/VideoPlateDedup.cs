using Nomeroff.Shared;

namespace MbWebApp.Services;

/// <summary>Состояние дедупликации номера по таймлайну видео (не по wall-clock).</summary>
internal sealed class VideoPlateDedupState
{
    public double LastTimeSec;
    public bool SavedWithGps;
    /// <summary>Последний сохранённый полный номер (для сравнения «лучше/хуже»).</summary>
    public string? LastSavedPlate;
}

/// <summary>
/// Дедуп по «стволу» номера (А123ВС… / 9036СС…), чтобы не писать Н767АК15 и Н767АК125 как разные авто.
/// </summary>
internal static class VideoPlateDedup
{
    public static string Key(string plateNorm) => PlateAlphabet.DedupStem(plateNorm);

    public static bool TryGetState(
        Dictionary<string, VideoPlateDedupState> cache,
        string plateNorm,
        out VideoPlateDedupState state) =>
        cache.TryGetValue(Key(plateNorm), out state!);

    public static bool IsWithinWindow(VideoPlateDedupState? state, double timeSec, int dedupSec) =>
        state != null && dedupSec > 0 && (timeSec - state.LastTimeSec) < dedupSec;

    public static bool IsDuplicateForDisplay(bool withinWindow, bool hasGps, VideoPlateDedupState? state) =>
        withinWindow && (state?.SavedWithGps == true || !hasGps);

    /// <summary>
    /// Сохранять: новое в окне; или повтор с GPS; или более полный номер того же ствола (9 vs 8 символов).
    /// </summary>
    public static bool ShouldSave(bool saveEnabled, bool withinWindow, bool hasGps, VideoPlateDedupState? state, string plateNorm)
    {
        if (!saveEnabled) return false;
        if (!withinWindow) return true;
        if (hasGps && state?.SavedWithGps != true) return true;
        // Улучшение чтения того же авто (Н767АК15 → Н767АК125)
        if (state?.LastSavedPlate != null
            && plateNorm.Length > state.LastSavedPlate.Length
            && Key(plateNorm) == Key(state.LastSavedPlate))
            return true;
        return false;
    }

    public static void NoteSighting(Dictionary<string, VideoPlateDedupState> cache, string plateNorm, double timeSec)
    {
        var k = Key(plateNorm);
        if (!cache.TryGetValue(k, out var s))
            s = new VideoPlateDedupState();
        s.LastTimeSec = timeSec;
        cache[k] = s;
    }

    public static void MarkSaved(Dictionary<string, VideoPlateDedupState> cache, string plateNorm, bool withGps)
    {
        var k = Key(plateNorm);
        if (!cache.TryGetValue(k, out var s))
            s = new VideoPlateDedupState();
        s.LastSavedPlate = plateNorm;
        if (withGps) s.SavedWithGps = true;
        cache[k] = s;
    }

    public static void MarkSavedWithGps(Dictionary<string, VideoPlateDedupState> cache, string plateNorm) =>
        MarkSaved(cache, plateNorm, withGps: true);
}
