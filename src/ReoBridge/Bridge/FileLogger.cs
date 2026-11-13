using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ReoBridge.Bridge;

/// <summary>Writes one log file per day (reobridge-yyyyMMdd.log) and deletes old ones.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly int _retainDays;
    private readonly BlockingCollection<string> _queue = new(10000);
    private readonly Thread _thread;

    public FileLoggerProvider(string dir, int retainDays)
    {
        _dir = dir;
        _retainDays = Math.Max(1, retainDays);
        Directory.CreateDirectory(dir);
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "ReoBridge log writer" };
        _thread.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        string? currentDay = null;
        StreamWriter? writer = null;
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                string day = DateTime.Now.ToString("yyyyMMdd");
                if (day != currentDay)
                {
                    writer?.Dispose();
                    writer = new StreamWriter(new FileStream(Path.Combine(_dir, $"reobridge-{day}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
                    currentDay = day;
                    Cleanup();
                }
                writer!.WriteLine(line);
                if (_queue.Count == 0) writer.Flush();
            }
        }
        catch (Exception) { }
        finally { writer?.Dispose(); }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "reobridge-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-_retainDays)) File.Delete(f);
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string level = logLevel switch
            {
                LogLevel.Information => "INF", LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "DBG",
            };
            string cat = category[(category.LastIndexOf('.') + 1)..];
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {cat}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Enqueue(line);
        }
    }
}
