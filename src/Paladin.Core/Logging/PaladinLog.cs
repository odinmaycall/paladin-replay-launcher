using System.Text;

namespace Paladin.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Line-oriented logger that mirrors to the console and to one or more log files.
/// Deliberately dependency free so Paladin.Core stays testable off-Windows.
/// </summary>
public sealed class PaladinLog : IDisposable
{
    private readonly List<StreamWriter> _writers = new();
    private readonly object _gate = new();
    private readonly bool _echoToConsole;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Raised for every emitted line so a UI can render progress without re-parsing files.</summary>
    public event Action<LogLevel, string>? LineWritten;

    public PaladinLog(bool echoToConsole = true) => _echoToConsole = echoToConsole;

    public void AddFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        lock (_gate) _writers.Add(writer);
    }

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message) => Write(LogLevel.Error, message);

    public void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message} :: {ex.GetType().Name}: {ex.Message}");

    public void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel) return;
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z  {Tag(level)}  {message}";
        lock (_gate)
        {
            foreach (var w in _writers)
            {
                try { w.WriteLine(line); } catch (IOException) { /* never let logging kill a restore */ }
            }
            if (_echoToConsole) Console.WriteLine(line);
        }
        LineWritten?.Invoke(level, message);
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        _ => "ERROR"
    };

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var w in _writers) { try { w.Dispose(); } catch (IOException) { } }
            _writers.Clear();
        }
    }
}
