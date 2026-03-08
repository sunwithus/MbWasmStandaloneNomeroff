namespace Nomeroff.Gps.Api;

/// <summary>Сервис для получения координат GPS (Serial/NMEA или другой источник).</summary>
public interface IGpsService
{
    /// <summary>Текущая позиция (из кэша последнего обновления).</summary>
    GpsPosition? CurrentPosition { get; }

    /// <summary>Событие при обновлении позиции.</summary>
    event EventHandler<GpsPosition>? PositionUpdated;

    /// <summary>Подключиться к устройству (COM-порт).</summary>
    Task<bool> ConnectAsync(string port, CancellationToken ct = default);

    /// <summary>Отключиться.</summary>
    Task DisconnectAsync();

    /// <summary>Подключено ли к устройству.</summary>
    bool IsConnected { get; }

    /// <summary>Список доступных COM-портов.</summary>
    Task<IReadOnlyList<string>> GetAvailablePortsAsync(CancellationToken ct = default);
}
