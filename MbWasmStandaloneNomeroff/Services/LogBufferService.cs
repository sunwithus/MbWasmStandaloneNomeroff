using System.Collections.Concurrent;
using System.Text;

namespace MbWasmStandaloneNomeroff.Services;

/// <summary>
/// Буфер логов для отладки в полевых условиях.
/// Хранит записи в памяти, позволяет скачать как текстовый файл.
/// </summary>
public class LogBufferService
{
    private const int MaxEntries = 5000;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public void Add(LogLevel level, string category, string message, Exception? exception = null)
    {
        var entry = new LogEntry(DateTime.UtcNow, level, category, message, exception);
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _)) { }
    }

    public string GetLogsAsText()
    {
        var sb = new StringBuilder();
        foreach (var e in _entries.ToArray())
        {
            sb.Append(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(" [");
            sb.Append(e.Level.ToString());
            sb.Append("] ");
            sb.Append(e.Category);
            sb.Append(": ");
            sb.Append(e.Message);
            if (e.Exception != null)
            {
                sb.AppendLine();
                sb.Append("  ");
                sb.Append(e.Exception.ToString());
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public void Clear() => _entries.Clear();

    private record LogEntry(DateTime Timestamp, LogLevel Level, string Category, string Message, Exception? Exception);
}
