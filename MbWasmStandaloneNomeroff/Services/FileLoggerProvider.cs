using Microsoft.Extensions.Logging;

namespace MbWasmStandaloneNomeroff.Services;

/// <summary>Провайдер логирования в буфер для последующей выгрузки в файл.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly LogBufferService _buffer;

    public FileLoggerProvider(LogBufferService buffer)
    {
        _buffer = buffer;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _buffer);

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly LogBufferService _buffer;

        public FileLogger(string category, LogBufferService buffer)
        {
            _category = category;
            _buffer = buffer;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            _buffer.Add(logLevel, _category, message, exception);
        }
    }
}
