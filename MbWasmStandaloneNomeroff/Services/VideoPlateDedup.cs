namespace MbWasmStandaloneNomeroff.Services;

internal sealed class VideoPlateDedupState
{
    public double LastTimeSec;
    public bool SavedWithGps;
}

internal static class VideoPlateDedup
{
    public static bool TryGetState(
        Dictionary<string, VideoPlateDedupState> cache,
        string plateNorm,
        out VideoPlateDedupState state) =>
        cache.TryGetValue(plateNorm, out state!);

    public static bool IsWithinWindow(VideoPlateDedupState? state, double timeSec, int dedupSec) =>
        state != null && dedupSec > 0 && (timeSec - state.LastTimeSec) < dedupSec;

    public static bool IsDuplicateForDisplay(bool withinWindow, bool hasGps, VideoPlateDedupState? state) =>
        withinWindow && (state?.SavedWithGps == true || !hasGps);

    public static bool ShouldSave(bool saveEnabled, bool withinWindow, bool hasGps, VideoPlateDedupState? state) =>
        saveEnabled && (!withinWindow || (hasGps && state?.SavedWithGps != true));

    public static void NoteSighting(Dictionary<string, VideoPlateDedupState> cache, string plateNorm, double timeSec)
    {
        if (!cache.TryGetValue(plateNorm, out var s))
            s = new VideoPlateDedupState();
        s.LastTimeSec = timeSec;
        cache[plateNorm] = s;
    }

    public static void MarkSavedWithGps(Dictionary<string, VideoPlateDedupState> cache, string plateNorm)
    {
        if (!cache.TryGetValue(plateNorm, out var s))
            s = new VideoPlateDedupState();
        s.SavedWithGps = true;
        cache[plateNorm] = s;
    }
}
