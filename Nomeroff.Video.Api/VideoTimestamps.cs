using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nomeroff.Video.Api;

/// <summary>Откуда взято время начала записи — нужно в логах, чтобы видеть деградацию.</summary>
public enum VideoStartSource
{
    None,
    FileName,
    ProbeCreationTime,
    FileWriteTime
}

/// <summary>
/// Время начала видеозаписи. Источники по убыванию надёжности:
/// имя файла регистратора, creation_time из ffprobe, время записи файла на диск.
/// Время кадра = начало записи + TimeSec. DateTime.Now не используется никогда.
/// </summary>
public static class VideoTimestamps
{
    /// <summary>NO20260707-145101-000033F.MP4 -> 2026-07-07 14:51:01 (локальное).</summary>
    private static readonly Regex FileNameStamp = new(
        @"(?<!\d)(\d{4})(\d{2})(\d{2})[-_ ]?(\d{2})(\d{2})(\d{2})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DateTime? TryParseFromFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var name = Path.GetFileNameWithoutExtension(path);
        foreach (var m in FileNameStamp.Matches(name).Cast<Match>())
        {
            if (TryBuild(m, out var dt))
                return dt;
        }
        return null;
    }

    private static bool TryBuild(Match m, out DateTime value)
    {
        value = default;
        var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var hour = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
        var second = int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);
        if (year is < 2000 or > 2100 || month is < 1 or > 12 || day is < 1 or > 31
            || hour > 23 || minute > 59 || second > 59)
            return false;
        try
        {
            value = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>creation_time из контейнера (ffprobe). Возвращается в UTC.</summary>
    public static async Task<DateTime?> TryProbeCreationTimeAsync(
        string ffprobePath,
        string videoPath,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                ArgumentList =
                {
                    "-v", "error",
                    "-show_entries", "format_tags=creation_time:stream_tags=creation_time",
                    "-of", "json",
                    videoPath
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;

            using var doc = JsonDocument.Parse(stdout);
            foreach (var raw in EnumerateCreationTimes(doc.RootElement))
            {
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                    return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            }
        }
        catch
        {
            // необязательный источник
        }
        return null;
    }

    private static IEnumerable<string> EnumerateCreationTimes(JsonElement root)
    {
        if (root.TryGetProperty("format", out var fmt)
            && fmt.TryGetProperty("tags", out var fmtTags)
            && fmtTags.TryGetProperty("creation_time", out var fmtTime)
            && fmtTime.GetString() is { } fv)
            yield return fv;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                if (s.TryGetProperty("tags", out var tags)
                    && tags.TryGetProperty("creation_time", out var t)
                    && t.GetString() is { } sv)
                    yield return sv;
            }
        }
    }

    /// <summary>Итоговое начало записи в UTC + откуда оно взято.</summary>
    public static async Task<(DateTime StartUtc, VideoStartSource Source)> ResolveStartUtcAsync(
        string videoPath,
        string ffprobePath,
        CancellationToken ct)
    {
        if (TryParseFromFileName(videoPath) is { } fromName)
            return (fromName.ToUniversalTime(), VideoStartSource.FileName);

        if (await TryProbeCreationTimeAsync(ffprobePath, videoPath, ct) is { } fromProbe)
            return (fromProbe, VideoStartSource.ProbeCreationTime);

        try
        {
            return (File.GetLastWriteTimeUtc(videoPath), VideoStartSource.FileWriteTime);
        }
        catch
        {
            return (DateTime.UtcNow, VideoStartSource.None);
        }
    }

    /// <summary>
    /// Время записи файла на диск — это момент копирования, а не съёмки, поэтому
    /// такому началу отсчёта OSD предпочтительнее.
    /// </summary>
    public static bool IsWeak(VideoStartSource source) =>
        source is VideoStartSource.None or VideoStartSource.FileWriteTime;

    /// <summary>
    /// Сверить время кадра из OSD с расчётным (начало + TimeSec).
    /// OSD доверяем только если расхождение в пределах допуска — иначе это ошибка OCR.
    /// </summary>
    public static bool OverlayAgreesWithExpected(
        DateTime? overlayLocal,
        DateTime expectedUtc,
        double toleranceSec = 90)
    {
        if (overlayLocal is not { } local)
            return false;
        var overlayUtc = DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        return Math.Abs((overlayUtc - expectedUtc).TotalSeconds) <= toleranceSec;
    }
}
