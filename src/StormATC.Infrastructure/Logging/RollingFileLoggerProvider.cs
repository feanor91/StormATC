using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using StormATC.Infrastructure.Security;

namespace StormATC.Infrastructure.Logging;

/// <summary>
/// Writes log entries to a dated file under the application-data directory, one file per day.
/// </summary>
/// <remarks>
/// <para>
/// A desktop application that has already shipped cannot be told "reproduce it with the debugger
/// attached". Without a log on disk, the only evidence of a problem is whatever the user
/// remembers, and the diagnostics export has nothing to point at.
/// </para>
/// <para>
/// <b>What is never written here.</b> This sink writes what the application logs, and the
/// application does not log API keys, <c>Authorization</c> headers, audio, or secret-store values
/// — <c>DpapiSecretStore</c> logs key <i>names</i>, and the provider clients report an error
/// category rather than a response body that might echo a header back. That discipline lives at
/// the call sites, which is the only place it can be enforced correctly; a redacting filter here
/// would give a false sense of safety while mangling legitimate text.
/// </para>
/// <para>
/// Writes are queued and drained by a single background task, so a slow disk cannot stall the
/// caller. Losing the last few lines when the process is killed outright is an acceptable trade
/// for never blocking a transmission on file I/O.
/// </para>
/// </remarks>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    /// <summary>Bounds memory if something logs in a tight loop faster than the disk drains.</summary>
    private const int MaximumQueuedEntries = 10_000;

    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly BlockingCollection<string> _queue = new(MaximumQueuedEntries);
    private readonly Task _writer;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>Creates the provider and starts the background writer.</summary>
    /// <param name="directory">Directory to write log files into. Created if it does not exist.</param>
    /// <param name="retentionDays">How many days of files to keep.</param>
    /// <param name="minimumLevel">The lowest level written to file.</param>
    public RollingFileLoggerProvider(
        string directory,
        int retentionDays,
        LogLevel minimumLevel = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        _minimumLevel = minimumLevel;

        TryCreateDirectory();
        RemoveExpiredFiles(retentionDays);

        _writer = Task.Run(DrainAsync);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(this, name));

    /// <summary>Queues one formatted entry. Never throws and never blocks.</summary>
    private void Enqueue(string entry)
    {
        if (_disposed)
        {
            return;
        }

        // Dropping an entry is better than blocking the thread that produced it. The alternative
        // — a bounded queue that waits — turns a logging hiccup into a UI freeze.
        _queue.TryAdd(entry);
    }

    private async Task DrainAsync()
    {
        var buffer = new StringBuilder();

        try
        {
            foreach (string entry in _queue.GetConsumingEnumerable())
            {
                buffer.Clear();
                buffer.Append(entry);

                // Take whatever else has piled up, so a burst becomes one write rather than many.
                while (buffer.Length < 32 * 1024 && _queue.TryTake(out string? queued))
                {
                    buffer.Append(queued);
                }

                await WriteAsync(buffer.ToString()).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // The queue was disposed during shutdown.
        }
        catch (InvalidOperationException)
        {
            // The queue was completed during shutdown.
        }
    }

    private async Task WriteAsync(string text)
    {
        try
        {
            string path = Path.Combine(
                _directory,
                $"stormatc-{DateTimeOffset.Now:yyyyMMdd}.log");

            await File.AppendAllTextAsync(path, text).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written must never take the application down with it.
        }
    }

    private void TryCreateDirectory()
    {
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Every write will then fail quietly, which is the correct outcome for a log sink.
        }
    }

    /// <summary>Deletes files older than the retention period.</summary>
    /// <remarks>
    /// Retention is enforced on startup rather than on a timer. A session that runs for days is
    /// not the case worth optimising, and a timer would keep the process awake for no benefit.
    /// </remarks>
    private void RemoveExpiredFiles(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            foreach (string file in Directory.EnumerateFiles(_directory, "stormatc-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Retention is housekeeping. Failing it must not stop the application starting.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _queue.CompleteAdding();

        try
        {
            // Bounded: shutdown must not hang on a disk that has stopped responding.
            _writer.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // The writer swallows its own failures; anything reaching here is not actionable.
        }

        _queue.Dispose();
        _loggers.Clear();
    }

    /// <summary>One category's view of the shared queue.</summary>
    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= provider._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder(256);

            builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff K", CultureInfo.InvariantCulture));
            builder.Append("  ").Append(Abbreviate(logLevel));
            builder.Append("  ").Append(category);
            builder.Append("  ").Append(SensitiveDataRedactor.Redact(formatter(state, exception)));
            builder.AppendLine();

            if (exception is not null)
            {
                builder.AppendLine(SensitiveDataRedactor.Redact(exception.ToString()));
            }

            provider.Enqueue(builder.ToString());
        }

        private static string Abbreviate(LogLevel level) => level switch
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
}
