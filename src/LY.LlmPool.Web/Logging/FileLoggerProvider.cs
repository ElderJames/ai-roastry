using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LY.LlmPool.Web.Logging;

/// <summary>
/// 简单的文件日志提供程序，用于将诊断日志写入本地文件。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly LogLevel _minimumLevel;
    private readonly BlockingCollection<string> _messageQueue = new();
    private readonly Task _processingTask;

    public FileLoggerProvider(string filePath, LogLevel minimumLevel = LogLevel.Information)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path must be provided", nameof(filePath));
        }

        _filePath = filePath;
        _minimumLevel = minimumLevel;

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _processingTask = Task.Run(ProcessQueueAsync);
    }

    public ILogger CreateLogger(string categoryName)
        => new FileLogger(categoryName, this, _minimumLevel);

    internal void EnqueueMessage(string message)
    {
        if (!_messageQueue.IsAddingCompleted)
        {
            _messageQueue.Add(message);
        }
    }

    private async Task ProcessQueueAsync()
    {
        foreach (var message in _messageQueue.GetConsumingEnumerable())
        {
            await File.AppendAllTextAsync(_filePath, message + Environment.NewLine);
        }
    }

    public void Dispose()
    {
        _messageQueue.CompleteAdding();
        try
        {
            _processingTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is IOException))
        {
            // ignore IO exceptions on shutdown
        }
        finally
        {
            _messageQueue.Dispose();
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly FileLoggerProvider _provider;
        private readonly LogLevel _minimumLevel;

        public FileLogger(string categoryName, FileLoggerProvider provider, LogLevel minimumLevel)
        {
            _categoryName = categoryName;
            _provider = provider;
            _minimumLevel = minimumLevel;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception == null)
            {
                return;
            }

            var record = $"{DateTimeOffset.Now:O} [{logLevel}] {_categoryName}: {message}";
            if (exception != null)
            {
                record += Environment.NewLine + exception;
            }

            _provider.EnqueueMessage(record);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose()
        {
            // no-op
        }
    }
}
