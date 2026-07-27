using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;

namespace StormATC.Desktop.Services;

/// <summary>
/// Reads and writes <c>usersettings.json</c>, the user-owned configuration layer.
/// </summary>
/// <remarks>
/// <para>
/// The file sits in the application data directory and overrides the shipped
/// <c>appsettings.json</c>, so a user's choices survive an upgrade. Values are written as a
/// section/key pair to keep the file shaped exactly like the configuration it overrides.
/// </para>
/// <para>
/// <b>API keys are never written here.</b> They belong to <see cref="ISecretStore"/>, which is
/// backed by an OS-protected store. This class refuses any key whose name suggests a secret, as
/// a defence against a future change routing one through by accident.
/// </para>
/// </remarks>
public sealed class UserSettingsService(IStoragePaths storagePaths, ILogger<UserSettingsService> logger)
    : IDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly IStoragePaths _storagePaths =
        storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

    private readonly ILogger<UserSettingsService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>The full path to the user settings file.</summary>
    public string SettingsFilePath =>
        Path.Combine(_storagePaths.ApplicationDataDirectory, "usersettings.json");

    /// <summary>
    /// Writes one configuration value, creating the file and section if needed.
    /// </summary>
    /// <param name="section">The configuration section, for example <c>Audio</c>.</param>
    /// <param name="key">The setting name within the section.</param>
    /// <param name="value">The value to store. Must not be a secret.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveAsync(
        string section,
        string key,
        JsonNode? value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (LooksLikeSecret(key))
        {
            throw new InvalidOperationException(
                $"'{section}:{key}' looks like a credential. Secrets must go to ISecretStore, "
                + "never to a settings file.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JsonObject root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);

            if (root[section] is not JsonObject sectionObject)
            {
                sectionObject = [];
                root[section] = sectionObject;
            }

            sectionObject[key] = value;

            await WriteRootAsync(root, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Writes several values in one pass, so the file is rewritten once.</summary>
    public async Task SaveManyAsync(
        IReadOnlyList<(string Section, string Key, JsonNode? Value)> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach ((string section, string key, _) in values)
        {
            if (LooksLikeSecret(key))
            {
                throw new InvalidOperationException(
                    $"'{section}:{key}' looks like a credential and cannot be written to settings.");
            }
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JsonObject root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);

            foreach ((string section, string key, JsonNode? value) in values)
            {
                if (root[section] is not JsonObject sectionObject)
                {
                    sectionObject = [];
                    root[section] = sectionObject;
                }

                sectionObject[key] = value;
            }

            await WriteRootAsync(root, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<JsonObject> ReadRootAsync(CancellationToken cancellationToken)
    {
        string path = SettingsFilePath;

        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt settings file must not stop the user changing a setting. Start fresh;
            // the shipped defaults still apply underneath.
            _logger.LogWarning(ex, "usersettings.json could not be read and will be replaced.");
            return [];
        }
    }

    private async Task WriteRootAsync(JsonObject root, CancellationToken cancellationToken)
    {
        _storagePaths.EnsureCreated();

        // Write to a temporary file and move it into place, so an interrupted write cannot
        // leave a half-written settings file behind.
        string path = SettingsFilePath;
        string temporary = path + ".tmp";

        await File.WriteAllTextAsync(temporary, root.ToJsonString(WriteOptions), cancellationToken)
            .ConfigureAwait(false);

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// True when a configuration key name suggests it carries a credential.
    /// </summary>
    /// <remarks>
    /// Deliberately does not reject every name containing "key" — <c>PushToTalkKey</c> is a
    /// keyboard binding, not a credential. It matches the credential-shaped names instead.
    /// </remarks>
    private static bool LooksLikeSecret(string key) =>
        key.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || key.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("credential", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();
}
