using System.Collections.Concurrent;
using System.Text;
using DBsync.Service.Configuration;
using Microsoft.Extensions.Logging;

namespace DBsync.Service.Logging;

/// <summary>
/// Minimal day-rolling file log under <c>%ProgramData%\DBsync\logs</c>. A headless service needs
/// somewhere to leave a trail that is not the Event Log, and pulling in a logging framework for
/// one file would be heavier than the whole feature.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 4096);
    private readonly LogLevel _minimum;
    private readonly int _retainDays;
    private readonly Thread _writer;
    private volatile bool _stopping;

    public FileLoggerProvider(LogLevel minimum = LogLevel.Information, int retainDays = 14)
    {
        _minimum = minimum;
        _retainDays = retainDays;
        ServicePaths.EnsureCreated();

        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "DBsync log writer" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Enqueue(string line)
    {
        if (_stopping) return;
        // Drop rather than block: losing a log line is always better than stalling a sync.
        _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        var lastPurge = DateTime.MinValue;

        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(ServicePaths.LogDirectory, $"dbsync-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);

                if (DateTime.UtcNow - lastPurge > TimeSpan.FromHours(6))
                {
                    lastPurge = DateTime.UtcNow;
                    Purge();
                }
            }
            catch
            {
                // Never let logging take the service down.
            }
        }
    }

    private void Purge()
    {
        var cutoff = DateTime.Now.AddDays(-_retainDays);
        foreach (var file in Directory.GetFiles(ServicePaths.LogDirectory, "dbsync-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch
            {
                // Locked or already gone.
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(Level(logLevel)).Append("] ")
                .Append(_category).Append(" — ")
                .Append(formatter(state, exception));

            if (exception is not null) builder.Append(Environment.NewLine).Append(exception);

            _provider.Enqueue(builder.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
