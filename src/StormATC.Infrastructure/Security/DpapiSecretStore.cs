using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;

namespace StormATC.Infrastructure.Security;

/// <summary>
/// Stores API keys encrypted with Windows DPAPI under the current user account.
/// </summary>
/// <remarks>
/// <para>
/// DPAPI ties the ciphertext to the logged-in Windows user, so the file is unreadable by other
/// accounts on the machine and useless if copied elsewhere. That is the right trade for a
/// desktop application: no master password to manage, no key material in the repository, and no
/// dependency on a running credential service.
/// </para>
/// <para>
/// Keys never appear in <c>appsettings.json</c>, in SQLite, in logs, in exception messages, or
/// in a diagnostics export. The only way a key leaves this class is through
/// <see cref="GetSecretAsync"/>, straight into an Authorization header.
/// </para>
/// </remarks>
public sealed class DpapiSecretStore : ISecretStore, IDisposable
{
    /// <summary>
    /// Additional entropy mixed into the DPAPI protection. Not a secret — it is compiled into
    /// the binary — but it scopes the ciphertext to StormATC so another application running as
    /// the same user cannot trivially decrypt it.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StormATC.SecretStore.v1");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IStoragePaths _storagePaths;
    private readonly ILogger<DpapiSecretStore> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>Creates the store.</summary>
    public DpapiSecretStore(IStoragePaths storagePaths, ILogger<DpapiSecretStore> logger)
    {
        _storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string SecretsFilePath => Path.Combine(_storagePaths.ApplicationDataDirectory, "secrets.dat");

    /// <inheritdoc />
    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Dictionary<string, string> secrets = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return secrets.TryGetValue(key, out string? value) ? value : null;
    }

    /// <inheritdoc />
    public async Task SetSecretAsync(string key, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> secrets = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            secrets[key] = value;
            await SaveUnsafeAsync(secrets, cancellationToken).ConfigureAwait(false);

            // Log the key name only. The value must never reach a log sink.
            _logger.LogInformation("Stored secret '{SecretKey}'.", key);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveSecretAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> secrets = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (secrets.Remove(key))
            {
                await SaveUnsafeAsync(secrets, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Removed secret '{SecretKey}'.", key);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> HasSecretAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Dictionary<string, string> secrets = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return secrets.ContainsKey(key);
    }

    /// <inheritdoc />
    public void Dispose() => _fileLock.Dispose();

    /// <summary>
    /// Masks a secret for display: first four and last four characters, everything else elided.
    /// </summary>
    /// <remarks>
    /// Short values are masked entirely rather than partially — showing six of eight characters
    /// would leak most of the secret.
    /// </remarks>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        if (secret.Length <= 12)
        {
            return new string('•', Math.Min(secret.Length, 16));
        }

        return $"{secret[..4]}{new string('•', 12)}{secret[^4..]}";
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>Reads and decrypts the store. Caller must hold <see cref="_fileLock"/>.</summary>
    private async Task<Dictionary<string, string>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        string path = SecretsFilePath;
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            byte[] protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);

            byte[] plaintext = ProtectedData.Unprotect(
                protectedBytes, Entropy, DataProtectionScope.CurrentUser);

            Dictionary<string, string>? secrets =
                JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext, SerializerOptions);

            return secrets ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // A store that cannot be decrypted — copied from another machine, or corrupted —
            // is treated as empty. The user re-enters their keys. Note that the exception is
            // logged without its message: DPAPI messages can echo back input.
            _logger.LogWarning(
                "The secret store could not be read ({ExceptionType}). Treating it as empty; API keys must be re-entered.",
                ex.GetType().Name);

            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Encrypts and writes the store. Caller must hold <see cref="_fileLock"/>.</summary>
    private async Task SaveUnsafeAsync(
        Dictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        _storagePaths.EnsureCreated();

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(secrets, SerializerOptions);
        byte[] protectedBytes = ProtectedData.Protect(
            plaintext, Entropy, DataProtectionScope.CurrentUser);

        // Write to a temporary file and move it into place, so an interrupted write cannot
        // leave a truncated store that would silently lose every key.
        string path = SecretsFilePath;
        string tempPath = path + ".tmp";

        await File.WriteAllBytesAsync(tempPath, protectedBytes, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);

        // Best effort: clear the plaintext copy from memory.
        Array.Clear(plaintext);
    }
}

/// <summary>
/// An in-memory secret store, so a test writes nothing to disk and makes no DPAPI call.
/// </summary>
/// <remarks>
/// <b>Tests only.</b> This is deliberately not registered in any application configuration. It
/// once stood in for the real store in mock mode, which meant a key the user had explicitly saved
/// was silently discarded on exit — bring-your-own-key does not work if the key does not survive
/// a restart. The application now always uses <see cref="DpapiSecretStore"/>, which writes no file
/// until there is something to write.
/// </remarks>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_secrets.TryGetValue(key, out string? value) ? value : null);
        }
    }

    /// <inheritdoc />
    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _secrets[key] = value;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveSecretAsync(string key, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _secrets.Remove(key);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> HasSecretAsync(string key, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_secrets.ContainsKey(key));
        }
    }
}
