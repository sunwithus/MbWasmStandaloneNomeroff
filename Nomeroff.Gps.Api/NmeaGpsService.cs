using System.IO.Ports;
using NmeaParser;
using NmeaParser.Messages;

namespace Nomeroff.Gps.Api;

/// <summary>
/// GPS-сервис для получения координат через NMEA 0183 из COM-порта.
/// </summary>
/// <remarks>
/// <para>
/// Использует библиотеку <see href="https://github.com/dotMorten/NmeaParser">NmeaParser</see>
/// (Apache 2.0) для разбора сообщений NMEA и обеспечивает совместимость с широким спектром
/// приёмников: GPS, Glonass, Galileo, BeiDou (BU-353, u-blox, Garmin и др.).
/// </para>
/// <para>
/// Поддерживаемые NMEA-сообщения:
/// </para>
/// <list type="bullet">
///   <item><b>GGA</b> — Global Positioning System Fix Data (широта, долгота, качество фиксации)</item>
///   <item><b>RMC</b> — Recommended Minimum (позиция, скорость, дата и время)</item>
/// </list>
/// <para>
/// Параметры COM-порта: 9600 бод, 8N1 (стандарт для большинства GPS).
/// Перед вызовом <see cref="ConnectAsync"/> рекомендуется получить список портов
/// через <see cref="GetAvailablePortsAsync"/>.
/// </para>
/// <para>
/// Реализует <see cref="IAsyncDisposable"/> — вызывайте <see cref="DisposeAsync"/>
/// при завершении работы (например, при остановке приложения).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using var gps = new NmeaGpsService();
/// var ports = await gps.GetAvailablePortsAsync();
/// if (await gps.ConnectAsync("COM3"))
/// {
///     gps.PositionUpdated += (s, p) => Console.WriteLine($"{p.Latitude}, {p.Longitude}");
///     var pos = gps.CurrentPosition; // последняя позиция
/// }
/// </code>
/// </example>
public class NmeaGpsService : IGpsService, IAsyncDisposable
{
    private readonly object _lock = new();
    private SerialPort? _serialPort;
    private NmeaDevice? _device;
    private GpsPosition? _currentPosition;

    /// <summary>
    /// Текущая позиция GPS (последняя полученная).
    /// </summary>
    /// <value>
    /// Позиция с широтой и долготой, либо <c>null</c>, если координаты ещё не получены
    /// или устройство отключено.
    /// </value>
    public GpsPosition? CurrentPosition
    {
        get { lock (_lock) return _currentPosition; }
    }

    /// <summary>
    /// Событие при обновлении позиции.
    /// </summary>
    /// <remarks>
    /// Вызывается при каждом приходе валидного NMEA-сообщения GGA или RMC с координатами.
    /// Может срабатывать часто (1–10 раз в секунду в зависимости от приёмника).
    /// </remarks>
    public event EventHandler<GpsPosition>? PositionUpdated;

    /// <summary>
    /// Подключено ли устройство к COM-порту.
    /// </summary>
    public bool IsConnected => _device?.IsOpen ?? false;

    /// <summary>
    /// Возвращает список доступных COM-портов.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Отсортированный список имён портов (например, COM1, COM3).</returns>
    /// <remarks>
    /// На Linux порты возвращаются как /dev/ttyUSB0, /dev/ttyACM0 и т.п.
    /// При ошибке доступа к списку портов возвращается пустой массив.
    /// </remarks>
    public Task<IReadOnlyList<string>> GetAvailablePortsAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                return (IReadOnlyList<string>)SerialPort.GetPortNames().OrderBy(x => x).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }, ct);
    }

    /// <summary>
    /// Подключается к GPS на указанном COM-порту.
    /// </summary>
    /// <param name="port">Имя порта (например, COM3 или /dev/ttyUSB0).</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns><c>true</c>, если подключение успешно; иначе <c>false</c>.</returns>
    /// <remarks>
    /// <para>
    /// Перед подключением выполняется <see cref="DisconnectAsync"/>, если устройство
    /// уже было подключено.
    /// </para>
    /// <para>
    /// Порт открывается с параметрами: 9600 бод, 8 бит данных, без чётности, 1 стоп-бит.
    /// Это стандарт для большинства NMEA-приёмников. После подключения приёмник может
    /// занять 10–60 секунд на поиск спутников и первую фиксацию.
    /// </para>
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">Нет доступа к порту.</exception>
    /// <exception cref="ArgumentException">Порт не найден или указан неверно.</exception>
    public async Task<bool> ConnectAsync(string port, CancellationToken ct = default)
    {
        await DisconnectAsync();
        if (string.IsNullOrWhiteSpace(port)) return false;

        try
        {
            _serialPort = new SerialPort(port, 9600, Parity.None, 8, StopBits.One);
            _serialPort.Open();
            _device = new StreamDevice(_serialPort.BaseStream);
            _device.MessageReceived += OnMessageReceived;
            await _device.OpenAsync();
            return true;
        }
        catch
        {
            await DisconnectAsync();
            return false;
        }
    }

    /// <summary>
    /// Отключается от GPS и освобождает ресурсы.
    /// </summary>
    /// <remarks>
    /// Останавливает чтение данных, закрывает COM-порт и сбрасывает
    /// <see cref="CurrentPosition"/>. Повторный вызов безопасен.
    /// </remarks>
    public async Task DisconnectAsync()
    {
        if (_device != null)
        {
            _device.MessageReceived -= OnMessageReceived;
            try { await _device.CloseAsync(); } catch { }
        }
        try
        {
            _serialPort?.Close();
            _serialPort?.Dispose();
        }
        catch { }
        _serialPort = null;
        _device?.Dispose();
        _device = null;
        lock (_lock) _currentPosition = null;
    }

    /// <summary>
    /// Обработчик входящих NMEA-сообщений: извлекает координаты из GGA и RMC.
    /// </summary>
    /// <param name="sender">Устройство-источник.</param>
    /// <param name="args">Аргументы с распарсенным сообщением.</param>
    private void OnMessageReceived(object? sender, NmeaMessageReceivedEventArgs args)
    {
        double? lat = null;
        double? lon = null;
        DateTimeOffset? ts = null;

        switch (args.Message)
        {
            case Gga gga when gga.Quality != Gga.FixQuality.Invalid:
                lat = gga.Latitude;
                lon = gga.Longitude;
                ts = DateTimeOffset.UtcNow.Date + gga.FixTime;
                break;
            case Rmc rmc when rmc.Active:
                lat = rmc.Latitude;
                lon = rmc.Longitude;
                ts = rmc.FixTime;
                break;
            default:
                return;
        }

        if (lat is { } la && lon is { } lo)
        {
            var pos = new GpsPosition(la, lo, ts ?? DateTimeOffset.UtcNow);
            lock (_lock) _currentPosition = pos;
            PositionUpdated?.Invoke(this, pos);
        }
    }

    /// <summary>
    /// Асинхронно освобождает ресурсы, отключая устройство.
    /// </summary>
    /// <remarks>
    /// Эквивалентно вызову <see cref="DisconnectAsync"/>. Рекомендуется вызывать
    /// при завершении работы сервиса (например, через <c>await using</c>).
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }
}
