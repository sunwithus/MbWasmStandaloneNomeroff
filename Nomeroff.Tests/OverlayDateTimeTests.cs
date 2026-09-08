using Nomeroff.Video.Api;
using Xunit;

namespace Nomeroff.Tests;

/// <summary>
/// Разбор даты/времени из OSD регистратора. Раньше эта регулярка не понимала
/// ДД-ММ-ГГГГ и пробелы между цифрами, поэтому времени в БД просто не было —
/// вместо него подставлялось «сейчас».
/// </summary>
public class OverlayDateTimeTests
{
    [Theory]
    [InlineData("07-07-2026 14:51:01", 2026, 7, 7, 14, 51, 1)]
    [InlineData("07-07-2026 14:51", 2026, 7, 7, 14, 51, 0)]
    [InlineData("2026-07-07 14:51:01", 2026, 7, 7, 14, 51, 1)]
    [InlineData("07 - 07 - 2026  14 : 51 : 01", 2026, 7, 7, 14, 51, 1)]
    [InlineData("07/07/2026 14.51.01", 2026, 7, 7, 14, 51, 1)]
    [InlineData("N 43.115 E131.885  07-07-2026 14:51:01", 2026, 7, 7, 14, 51, 1)]
    public void ParseOverlayDateTime_ReadsRecorderFormats(
        string text, int year, int month, int day, int hour, int minute, int second)
    {
        var parsed = GpsOverlayOcrParser.ParseOverlayDateTime(text);
        Assert.Equal(new DateTime(year, month, day, hour, minute, second), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("N 43.115 E 131.885")]
    [InlineData("99-99-2026 14:51:01")]
    public void ParseOverlayDateTime_RejectsGarbage(string text)
        => Assert.Null(GpsOverlayOcrParser.ParseOverlayDateTime(text));

    [Theory]
    [InlineData("NO20260707-145101-000033F.MP4", 2026, 7, 7, 14, 51, 1)]
    [InlineData(@"D:\REG_VIDEO\NO20260707-145101-000033F.MP4", 2026, 7, 7, 14, 51, 1)]
    [InlineData("20260707_145101.mp4", 2026, 7, 7, 14, 51, 1)]
    public void TryParseFromFileName_ReadsRecorderNaming(
        string path, int year, int month, int day, int hour, int minute, int second)
    {
        var parsed = VideoTimestamps.TryParseFromFileName(path);
        Assert.Equal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local), parsed);
    }

    [Theory]
    [InlineData("video.mp4")]
    [InlineData("clip-12345.mp4")]
    public void TryParseFromFileName_ReturnsNullWithoutStamp(string path)
        => Assert.Null(VideoTimestamps.TryParseFromFileName(path));

    [Fact]
    public void OverlayAgreesWithExpected_AcceptsSmallDriftAndRejectsBigOne()
    {
        var local = new DateTime(2026, 7, 7, 14, 51, 1, DateTimeKind.Local);
        var expectedUtc = local.ToUniversalTime();

        Assert.True(VideoTimestamps.OverlayAgreesWithExpected(local.AddSeconds(30), expectedUtc));
        Assert.False(VideoTimestamps.OverlayAgreesWithExpected(local.AddHours(3), expectedUtc));
        Assert.False(VideoTimestamps.OverlayAgreesWithExpected(null, expectedUtc));
    }

    [Theory]
    [InlineData("131°55.405'E,43°11.349'N", 43.18915, 131.923416)]
    [InlineData("43°11.349'N, 131°55.405'E", 43.18915, 131.923416)]
    public void ParseCoordinates_ReadsRecorderOsd(string text, double lat, double lon)
    {
        var (gotLat, gotLon) = GpsOverlayOcrParser.ParseCoordinates(text);
        Assert.NotNull(gotLat);
        Assert.NotNull(gotLon);
        Assert.InRange(gotLat!.Value, lat - 0.001, lat + 0.001);
        Assert.InRange(gotLon!.Value, lon - 0.001, lon + 0.001);
    }

    [Fact]
    public void ParseCoordinates_DoesNotInventVladivostokLongitude()
    {
        // Обрывок без градусов долготы раньше достраивался константой 131.0.
        var (lat, lon) = GpsOverlayOcrParser.ParseCoordinates("55.405,N43.189");
        Assert.Null(lon);
        Assert.True(lat is null or >= 40);
    }
}
