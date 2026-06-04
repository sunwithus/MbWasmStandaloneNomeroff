namespace Nomeroff.Video.Api;

public sealed class OverlayOcrResult
{
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    /// <summary>Время с оверлея (локальное время регистратора, Kind=Unspecified).</summary>
    public DateTime? OverlayTime { get; init; }

    public bool HasGps => Latitude.HasValue && Longitude.HasValue;
}
