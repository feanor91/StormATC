using Microsoft.Extensions.Logging;
using StormATC.Infrastructure.Logging;

namespace StormATC.Infrastructure.Tests;

/// <summary>The rolling file log the diagnostics story depends on.</summary>
public class RollingFileLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "stormatc-logtests", Guid.CreateVersion7().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file still held open is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Writes, then disposes, which is what flushes the queue.</summary>
    private void WriteThenFlush(Action<ILogger> write, int retentionDays = 7)
    {
        using var provider = new RollingFileLoggerProvider(_directory, retentionDays);
        write(provider.CreateLogger("Test.Category"));
    }

    private string ReadTheLog()
    {
        string[] files = Directory.GetFiles(_directory, "stormatc-*.log");
        Assert.Single(files);
        return File.ReadAllText(files[0]);
    }

    [Fact]
    public void An_entry_reaches_the_file()
    {
        WriteThenFlush(logger => logger.LogInformation("Runway {Runway} is in use.", "18"));

        string contents = ReadTheLog();

        Assert.Contains("Runway 18 is in use.", contents, StringComparison.Ordinal);
        Assert.Contains("info", contents, StringComparison.Ordinal);
        Assert.Contains("Test.Category", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_is_written_in_full()
    {
        // The stack trace is the whole reason a log file is worth having.
        WriteThenFlush(logger => logger.LogError(
            new InvalidOperationException("the taxi graph is disconnected"),
            "Routing failed."));

        string contents = ReadTheLog();

        Assert.Contains("Routing failed.", contents, StringComparison.Ordinal);
        Assert.Contains("the taxi graph is disconnected", contents, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_below_the_minimum_level_are_not_written()
    {
        using (var provider = new RollingFileLoggerProvider(_directory, 7, LogLevel.Warning))
        {
            ILogger logger = provider.CreateLogger("Test.Category");
            logger.LogDebug("Chatter that should never reach the disk.");
            logger.LogWarning("Something worth keeping.");
        }

        string contents = ReadTheLog();

        Assert.DoesNotContain("Chatter", contents, StringComparison.Ordinal);
        Assert.Contains("Something worth keeping.", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_older_than_the_retention_period_is_removed_at_startup()
    {
        Directory.CreateDirectory(_directory);

        string stale = Path.Combine(_directory, "stormatc-20200101.log");
        File.WriteAllText(stale, "an old session");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));

        string recent = Path.Combine(_directory, "stormatc-20200102.log");
        File.WriteAllText(recent, "a recent session");
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-1));

        using var provider = new RollingFileLoggerProvider(_directory, retentionDays: 7);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Retention_of_zero_days_keeps_everything()
    {
        Directory.CreateDirectory(_directory);

        string old = Path.Combine(_directory, "stormatc-20200101.log");
        File.WriteAllText(old, "an old session");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-4000));

        // Zero means "no retention policy", not "delete everything" — the destructive reading of
        // an unset value is exactly the one that loses a user's evidence.
        using var provider = new RollingFileLoggerProvider(_directory, retentionDays: 0);

        Assert.True(File.Exists(old));
    }

    [Fact]
    public void A_directory_that_cannot_be_created_does_not_throw()
    {
        // A log sink that can take the application down with it is worse than no log sink.
        string impossible = Path.Combine(_directory, "a.txt", "nested");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "a.txt"), "not a directory");

        using var provider = new RollingFileLoggerProvider(impossible, 7);
        provider.CreateLogger("Test.Category").LogInformation("Goes nowhere, quietly.");
    }
}
