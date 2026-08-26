using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Mcd.Core.Infrastructure;

/// <summary>
/// A log file, plus the markers the CI smoke test greps for. Deliberately not a
/// logging framework: the release build has to answer "did the AppBar get
/// removed" from a text file on a user's machine, and nothing more.
/// </summary>
public sealed class FileLogger : ILoggerProvider
{
    private const long MaxBytes = 512 * 1024;
    private const int KeptGenerations = 2;

    private readonly string _path;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Thread _writer;

    public FileLogger(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.LogDirectory, "mcd.log");

        // One thread owns the file. Log lines arrive from the UI thread, the
        // sensor thread and the display watcher's message loop.
        _writer = new Thread(WriteLoop)
        {
            Name = "mcd-log",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private void Enqueue(LogLevel level, string category, string message, Exception? error)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" LEVEL=").Append(Abbreviate(level))
            .Append(' ').Append(category)
            .Append(" | ").Append(message);

        if (error is not null)
        {
            line.Append(Environment.NewLine).Append(error);
        }

        if (!_queue.IsAddingCompleted)
        {
            _queue.Add(line.ToString());
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "NONE",
    };

    private void WriteLoop()
    {
        foreach (string line in _queue.GetConsumingEnumerable())
        {
            try
            {
                Rotate();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // A log that cannot be written must not take the program with it.
            }
        }
    }

    private void Rotate()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        for (int i = KeptGenerations; i >= 1; i--)
        {
            string older = $"{_path}.{i}";
            string newer = i == 1 ? _path : $"{_path}.{i - 1}";
            if (File.Exists(newer))
            {
                File.Move(newer, older, overwrite: true);
            }
        }
    }

    private sealed class Sink(FileLogger owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            owner.Enqueue(logLevel, category, formatter(state, exception), exception);
        }
    }
}
