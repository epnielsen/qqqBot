using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace qqqBot;

/// <summary>
/// Simple file logger provider for writing logs to disk.
/// Thread-safe, appends to file with timestamp prefix.
/// </summary>
public class FileLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// When set, log timestamps use this value instead of DateTime.Now.
    /// Used in replay mode so log entries reflect the replayed tick time.
    /// </summary>
    public static DateTime? ClockOverride { get; set; }

    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    
    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
        // Open file for append, create if doesn't exist
        _writer = new StreamWriter(
            new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
            System.Text.Encoding.UTF8)
        {
            AutoFlush = true
        };
    }
    
    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));
    }
    
    public void WriteLog(string category, LogLevel level, string message)
    {
        lock (_lock)
        {
            var timestamp = (ClockOverride ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss.fff");
            var levelStr = level switch
            {
                LogLevel.Trace => "TRCE",
                LogLevel.Debug => "DBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "FAIL",
                LogLevel.Critical => "CRIT",
                _ => "NONE"
            };
            
            // Shorten category name for cleaner output
            var shortCategory = category.Contains('.') 
                ? category.Substring(category.LastIndexOf('.') + 1) 
                : category;
            
            _writer.WriteLine($"{timestamp} [{levelStr}] {shortCategory}: {message}");
        }
    }
    
    public void Dispose()
    {
        _loggers.Clear();
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}

/// <summary>
/// Individual logger instance for a specific category.
/// </summary>
public class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLoggerProvider _provider;
    
    public FileLogger(string categoryName, FileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }
    
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        
        var message = formatter(state, exception);
        if (exception != null)
        {
            message += Environment.NewLine + exception.ToString();
        }
        
        _provider.WriteLog(_categoryName, logLevel, message);
    }
}
