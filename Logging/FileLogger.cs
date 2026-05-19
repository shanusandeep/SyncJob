using System.Globalization;

namespace SyncExamSubJob.Logging;

/// <summary>
/// Minimal dependency-free logger: writes every line to the console and to a
/// date-stamped file (SyncExamSubJob_yyyyMMdd.log) in the configured directory.
/// Task Scheduler runs unattended, so a durable file trail is essential.
/// </summary>
public sealed class FileLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public string LogFilePath { get; }

    public FileLogger(string logDirectory)
    {
        // Resolve relative paths against the executable directory so the log
        // location is stable regardless of Task Scheduler's working directory.
        var dir = Path.IsPathRooted(logDirectory)
            ? logDirectory
            : Path.Combine(AppContext.BaseDirectory, logDirectory);

        Directory.CreateDirectory(dir);

        LogFilePath = Path.Combine(
            dir, $"SyncExamSubJob_{DateTime.Now:yyyyMMdd}.log");

        // ReadWrite share: a second instance (which will exit on the app-lock
        // check) must not crash here with a file sharing violation.
        _writer = new StreamWriter(
            new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");

    private void Write(string level, string message)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}",
            DateTime.Now, level, message);

        lock (_gate)
        {
            if (_disposed) return;
            Console.WriteLine(line);
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
