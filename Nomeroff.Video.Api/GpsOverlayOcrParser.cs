using System.Globalization;
using System.Text.RegularExpressions;

namespace Nomeroff.Video.Api;

public static class GpsOverlayOcrParser
{
    private static readonly Regex EnDirLonLat = new(
        @"E\s*(\d{1,3}[.,]\d{1,8})\s*[,;]?\s*N\s*(\d{1,2}[.,]\d{1,8})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Neoline OSD: E131-9613,N43-1110 (дефис вместо точки между градусами и дробью).</summary>
    private static readonly Regex EnDashLonLat = new(
        @"E\s*(\d{1,3})\s*-\s*(\d{3,6})\s*[,;]?\s*N\s*(\d{1,2})\s*[-.](\d{3,6})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Регистратор: 131°56.944'E, 43°12.989'N (градусы + десятичные минуты).
    /// Порядок: долгота E, широта N.
    /// </summary>
    private static readonly Regex DegreeMinuteLonLat = new(
        @"(\d{1,3})\s*[°ºOoo˚]?\s*(\d{1,2}[.,]\d{1,5})\s*['′`'’]*\s*E\s*[,;]?\s*(\d{1,2})\s*[°ºOoo˚]?\s*(\d{1,2}[.,]\d{1,5})\s*['′`'’]*\s*N",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Вариант N сначала: 43°12.989'N, 131°56.944'E</summary>
    private static readonly Regex DegreeMinuteLatLon = new(
        @"(\d{1,2})\s*[°ºOoo˚]?\s*(\d{1,2}[.,]\d{1,5})\s*['′`'’]*\s*N\s*[,;]?\s*(\d{1,3})\s*[°ºOoo˚]?\s*(\d{1,2}[.,]\d{1,5})\s*['′`'’]*\s*E",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LonFragBeforeN = new(
        @"(?<![.\d])(\d{3})\s*,\s*N\s*(\d{1,2})(?:[.,](\d{1,4}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DateTimeOverlay = new(
        @"(\d{4})\s*[-/.]\s*(\d{1,2})\s*[-/.]\s*(\d{1,2})\s+(\d{1,2})\s*[.:]\s*(\d{2})(?:\s*[.:]\s*(\d{2}))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DateTime? ParseOverlayDateTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = CollapseDigits(text.Replace('\r', ' ').Replace('\n', ' '));
        var m = DateTimeOverlay.Match(s);
        if (!m.Success) return null;

        var sec = m.Groups[6].Success ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
        try
        {
            return new DateTime(
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                sec,
                DateTimeKind.Unspecified);
        }
        catch
        {
            return null;
        }
    }

    public static (double? Latitude, double? Longitude) ParseCoordinates(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);

        // Сначала пробуем °/' на «сыром» тексте — CollapseDigits склеит 131 56.944 → 13156.944
        var soft = SoftNormalize(text);
        var dmLonLat = DegreeMinuteLonLat.Match(soft);
        if (dmLonLat.Success
            && TryParseDegreeMinutes(dmLonLat.Groups[3].Value, dmLonLat.Groups[4].Value, isLatitude: true, out var latDm1)
            && TryParseDegreeMinutes(dmLonLat.Groups[1].Value, dmLonLat.Groups[2].Value, isLatitude: false, out var lonDm1)
            && IsValidPair(latDm1, lonDm1))
            return (latDm1, lonDm1);

        var dmLatLon = DegreeMinuteLatLon.Match(soft);
        if (dmLatLon.Success
            && TryParseDegreeMinutes(dmLatLon.Groups[1].Value, dmLatLon.Groups[2].Value, isLatitude: true, out var latDm2)
            && TryParseDegreeMinutes(dmLatLon.Groups[3].Value, dmLatLon.Groups[4].Value, isLatitude: false, out var lonDm2)
            && IsValidPair(latDm2, lonDm2))
            return (latDm2, lonDm2);

        var normalized = NormalizeGpsText(text);

        dmLonLat = DegreeMinuteLonLat.Match(normalized);
        if (dmLonLat.Success
            && TryParseDegreeMinutes(dmLonLat.Groups[3].Value, dmLonLat.Groups[4].Value, isLatitude: true, out latDm1)
            && TryParseDegreeMinutes(dmLonLat.Groups[1].Value, dmLonLat.Groups[2].Value, isLatitude: false, out lonDm1)
            && IsValidPair(latDm1, lonDm1))
            return (latDm1, lonDm1);

        dmLatLon = DegreeMinuteLatLon.Match(normalized);
        if (dmLatLon.Success
            && TryParseDegreeMinutes(dmLatLon.Groups[1].Value, dmLatLon.Groups[2].Value, isLatitude: true, out latDm2)
            && TryParseDegreeMinutes(dmLatLon.Groups[3].Value, dmLatLon.Groups[4].Value, isLatitude: false, out lonDm2)
            && IsValidPair(latDm2, lonDm2))
            return (latDm2, lonDm2);

        var en = EnDirLonLat.Match(normalized);
        if (en.Success
            && TryParseCoord(en.Groups[2].Value, out var latEn)
            && TryParseCoord(en.Groups[1].Value, out var lonEn)
            && IsValidPair(latEn, lonEn))
            return (latEn, lonEn);

        var enDash = EnDashLonLat.Match(normalized);
        if (enDash.Success
            && TryNeolineFraction(enDash.Groups[3].Value, enDash.Groups[4].Value, isLatitude: true, out var latDash)
            && TryNeolineFraction(enDash.Groups[1].Value, enDash.Groups[2].Value, isLatitude: false, out var lonDash)
            && IsValidPair(latDash, lonDash))
            return (latDash, lonDash);

        var frag = LonFragBeforeN.Match(normalized);
        if (frag.Success)
        {
            var latStr = frag.Groups[3].Success
                ? $"{frag.Groups[2].Value}.{frag.Groups[3].Value}"
                : frag.Groups[2].Value;
            if (int.TryParse(frag.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lonTail)
                && TryParseCoord(latStr, out var latF)
                && lonTail is >= 500 and <= 999)
            {
                var lonF = 131.0 + lonTail / 1000.0;
                if (IsValidPair(latF, lonF))
                    return (latF, lonF);
            }
        }

        var lonM = Regex.Match(normalized, @"E\s*(\d{1,3}[.,]\d{1,8})", RegexOptions.IgnoreCase);
        var latM = Regex.Match(normalized, @"N\s*(\d{1,2}[.,]\d{1,8})", RegexOptions.IgnoreCase);
        if (lonM.Success && latM.Success
            && TryParseCoord(latM.Groups[1].Value, out var latL)
            && TryParseCoord(lonM.Groups[1].Value, out var lonL)
            && IsValidPair(latL, lonL))
            return (latL, lonL);

        var lonDashM = Regex.Match(normalized, @"E\s*(\d{1,3})\s*-\s*(\d{3,6})", RegexOptions.IgnoreCase);
        var latDashM = Regex.Match(normalized, @"N\s*(\d{1,2})\s*[-.](\d{3,6})", RegexOptions.IgnoreCase);
        if (lonDashM.Success && latDashM.Success
            && TryNeolineFraction(latDashM.Groups[1].Value, latDashM.Groups[2].Value, isLatitude: true, out var latLD)
            && TryNeolineFraction(lonDashM.Groups[1].Value, lonDashM.Groups[2].Value, isLatitude: false, out var lonLD)
            && IsValidPair(latLD, lonLD))
            return (latLD, lonLD);

        return (null, null);
    }

    private static string SoftNormalize(string text)
    {
        var s = text.Replace('\r', ' ').Replace('\n', ' ');
        s = s.Replace('`', '\'').Replace('′', '\'').Replace('’', '\'');
        s = s.Replace('º', '°').Replace('˚', '°');
        s = Regex.Replace(s, @"(\d)\s*[Oo0]\s*(\d{1,2}[.,]\d)", "$1°$2");
        s = Regex.Replace(s, @"([.,])\s+(\d)", "$1$2");
        return s;
    }

    private static string NormalizeGpsText(string text)
    {
        var s = text.Replace('\r', ' ').Replace('\n', ' ');
        // OCR часто путает ° с o/0 и минуты с `
        s = s.Replace('`', '\'').Replace('′', '\'').Replace('’', '\'');
        s = s.Replace('º', '°').Replace('˚', '°');
        s = Regex.Replace(s, @"(\d)\s*[Oo0]\s*(\d{1,2}[.,]\d)", "$1°$2");
        s = s.Replace('O', '0').Replace('o', '0');
        s = Regex.Replace(s, @"(?:km\s*/\s*h|km/h)\s*e?s\s*(\d)", " E$1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"ENS(\d)", "E1$1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bE\s*l(\d)", "E$1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bN\s*l(\d)", "N$1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\dN(\d)", " N$1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"N\s*(\d{1,2})\s*,\s*(\d{3,4})\b", "N$1.$2", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"N(\d{1,2})\.\-+(\d)", "N$1.$2", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"E(\d{1,3})\.\-+(\d)", "E$1.$2", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(\d)\s+-\s*(\d)", "$1-$2");
        s = Regex.Replace(s, @"\bE\s*(\d)", "E$1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bN\s*(\d)", "N$1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([.,])\s+(\d)", "$1$2");
        return CollapseDigits(s);
    }

    private static string CollapseDigits(string s)
    {
        for (var i = 0; i < 8; i++)
        {
            var next = Regex.Replace(s, @"(?<=\d)\s+(?=\d)", "");
            if (next == s) break;
            s = next;
        }

        return s;
    }

    private static bool TryParseCoord(string s, out double value)
    {
        value = 0;
        s = s.Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>131° + 56.944' → 131 + 56.944/60.</summary>
    private static bool TryParseDegreeMinutes(string degrees, string minutes, bool isLatitude, out double value)
    {
        value = 0;
        if (!TryParseCoord(degrees, out var deg) || !TryParseCoord(minutes, out var min))
            return false;
        if (min is < 0 or >= 60)
            return false;

        value = deg + min / 60.0;
        return isLatitude
            ? value is >= 40 and <= 70
            : value is >= 100 and <= 180;
    }

    /// <summary>Градусы + дробная часть без точки: 131 + 9613/10^4 → 131.9613.</summary>
    private static bool TryNeolineFraction(string degrees, string fraction, bool isLatitude, out double value)
    {
        value = 0;
        if (!int.TryParse(degrees, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deg))
            return false;

        var frac = fraction.Trim();
        if (frac.Length > 4)
            frac = frac[..4];

        if (!int.TryParse(frac, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fracInt))
            return false;

        value = deg + fracInt / Math.Pow(10, frac.Length);
        return isLatitude
            ? value is >= 40 and <= 70
            : value is >= 100 and <= 180;
    }

    private static bool IsValidPair(double lat, double lon) =>
        lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
}
