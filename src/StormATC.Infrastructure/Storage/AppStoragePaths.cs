using System.Reflection;
using StormATC.Application.Abstractions;

namespace StormATC.Infrastructure.Storage;

/// <summary>
/// Resolves StormATC's writable locations under the per-user application-data directory.
/// </summary>
/// <remarks>
/// <para>
/// Everything user-specific lives under <c>%LOCALAPPDATA%\StormATC</c>. Nothing is written next
/// to the executable, so the application works from a read-only install location and one
/// machine can serve several user accounts without them sharing history.
/// </para>
/// <para>
/// Prompts and sample data are read from beside the executable, because they ship with the
/// application and are meant to be edited in place by contributors.
/// </para>
/// </remarks>
public sealed class AppStoragePaths : IStoragePaths
{
    /// <summary>Creates paths rooted at the real per-user application-data directory.</summary>
    public AppStoragePaths()
    {
        ApplicationDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StormATC");

        string contentRoot = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
            ?? AppContext.BaseDirectory;

        PromptDirectory = Path.Combine(contentRoot, "prompts");
        DataDirectory = Path.Combine(contentRoot, "data");
    }

    /// <summary>
    /// Creates paths rooted at a caller-supplied directory. Used by tests so they never touch
    /// the real user profile.
    /// </summary>
    public AppStoragePaths(string applicationDataDirectory, string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        ApplicationDataDirectory = applicationDataDirectory;
        PromptDirectory = Path.Combine(contentRoot, "prompts");
        DataDirectory = Path.Combine(contentRoot, "data");
    }

    /// <inheritdoc />
    public string ApplicationDataDirectory { get; }

    /// <inheritdoc />
    public string DatabasePath => Path.Combine(ApplicationDataDirectory, "stormatc.db");

    /// <inheritdoc />
    public string LogDirectory => Path.Combine(ApplicationDataDirectory, "logs");

    /// <inheritdoc />
    public string FlightPlanCacheDirectory => Path.Combine(ApplicationDataDirectory, "flightplans");

    /// <inheritdoc />
    public string PromptDirectory { get; }

    /// <inheritdoc />
    public string DataDirectory { get; }

    /// <inheritdoc />
    public void EnsureCreated()
    {
        Directory.CreateDirectory(ApplicationDataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(FlightPlanCacheDirectory);
    }
}
