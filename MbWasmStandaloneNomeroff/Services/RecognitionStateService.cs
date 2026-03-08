using Microsoft.Extensions.Logging;

namespace MbWasmStandaloneNomeroff.Services;

/// <summary>Контроллер распознавания — регистрируется Home.razor.</summary>
public interface IRecognitionRunner
{
    Task StartAsync();
    void Stop();
}

/// <summary>
/// Глобальный сервис состояния рабочего режима распознавания.
/// Хранит статус камеры, GPS, API и ошибок. Обновляется из Home.razor и Layout.
/// </summary>
public class RecognitionStateService
{
    private bool _isRunning;
    private string _cameraStatus = "—";
    private string _gpsStatus = "—";
    private string _apiStatus = "—";
    private string _dbStatus = "—";
    private string? _lastError;
    public bool IsRunning
    {
        get => _isRunning;
        set { _isRunning = value; OnChanged(); }
    }

    public string CameraStatus
    {
        get => _cameraStatus;
        set { _cameraStatus = value; OnChanged(); }
    }

    public string GpsStatus
    {
        get => _gpsStatus;
        set { _gpsStatus = value; OnChanged(); }
    }

    public string ApiStatus
    {
        get => _apiStatus;
        set { _apiStatus = value; OnChanged(); }
    }

    public string DbStatus
    {
        get => _dbStatus;
        set { _dbStatus = value; OnChanged(); }
    }

    public string? LastError
    {
        get => _lastError;
        set { _lastError = value; OnChanged(); }
    }

    /// <summary>Событие при изменении состояния (для обновления UI).</summary>
    public event Action? OnStateChanged;

    private IRecognitionRunner? _runner;
    private readonly System.Timers.Timer _refreshTimer;
    private readonly ILogger<RecognitionStateService>? _logger;

    public RecognitionStateService(ILogger<RecognitionStateService>? logger = null)
    {
        _logger = logger;
        _refreshTimer = new System.Timers.Timer(2000);
        _refreshTimer.Elapsed += (_, _) => OnChanged();
        _refreshTimer.Start();
    }

    public void RegisterRunner(IRecognitionRunner runner)
    {
        _runner = runner;
        _logger?.LogInformation("RegisterRunner: runner зарегистрирован, PendingStart={Pending}", _pendingStart);
        if (_pendingStart)
        {
            _pendingStart = false;
            _ = _runner.StartAsync();
        }
    }

    public void UnregisterRunner()
    {
        _runner = null;
        _logger?.LogDebug("UnregisterRunner");
    }

    /// <summary>Запуск при автостарте — отложенный до регистрации runner.</summary>
    public bool PendingStart
    {
        get => _pendingStart;
        set { _pendingStart = value; OnChanged(); }
    }
    private bool _pendingStart;

    public async Task RequestStartAsync()
    {
        if (_runner != null)
        {
            _logger?.LogInformation("RequestStartAsync: вызываем runner.StartAsync");
            await _runner.StartAsync();
        }
        else
        {
            _logger?.LogInformation("RequestStartAsync: runner=null, устанавливаем PendingStart=true");
            _pendingStart = true;
        }
    }

    public void RequestStop() => _runner?.Stop();

    private void OnChanged() => OnStateChanged?.Invoke();

    public void SetError(string? error)
    {
        _lastError = error;
        OnChanged();
    }

    public void ClearError()
    {
        _lastError = null;
        OnChanged();
    }
}
