using System.Collections.Concurrent;

namespace Quipu.Services;

/// <summary>Minimal logger: keeps the last lines in memory (for the API/dashboard) and appends to quipu.log.</summary>
public sealed class AppLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _ring = new();
    private readonly object _fileGate = new();
    private readonly string? _file;

    public AppLogProvider(string? logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory)) return;
        try
        {
            Directory.CreateDirectory(logDirectory);
            _file = Path.Combine(logDirectory, "quipu.log");
            if (File.Exists(_file) && new FileInfo(_file).Length > 10 << 20) File.Move(_file, _file + ".1", true);
        }
        catch
        {
            _file = null;
        }
    }

    public IReadOnlyList<string> Tail(int n) => _ring.Reverse().Take(n).Reverse().ToList();

    internal void Write(string line)
    {
        _ring.Enqueue(line);
        while (_ring.Count > 1000) _ring.TryDequeue(out _);
        if (_file is null) return;
        lock (_fileGate)
        {
            try { File.AppendAllText(_file, line + Environment.NewLine); } catch { }
        }
    }

    public ILogger CreateLogger(string categoryName) => new AppLogger(this, categoryName);
    public void Dispose() { }

    private sealed class AppLogger(AppLogProvider owner, string category) : ILogger
    {
        private readonly string _short = category.Split('.').Last();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) =>
            level >= (category.StartsWith("Microsoft", StringComparison.Ordinal) || category.StartsWith("System", StringComparison.Ordinal)
                ? LogLevel.Warning
                : LogLevel.Information);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var lvl = level switch
            {
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                LogLevel.Debug => "DBG",
                _ => "INF",
            };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {lvl} {_short}: {formatter(state, ex)}";
            if (ex is not null) line += " | " + ex.GetType().Name + ": " + ex.Message;
            owner.Write(line);
        }
    }
}
