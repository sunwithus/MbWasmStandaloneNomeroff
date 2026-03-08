namespace Nomeroff.Gps.Api;

/// <summary>Текущая позиция GPS.</summary>
public record GpsPosition(double Latitude, double Longitude, DateTimeOffset? Timestamp = null);
