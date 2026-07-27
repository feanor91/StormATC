using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;

namespace StormATC.Infrastructure.Prompts;

/// <summary>
/// Loads controller prompts from Markdown files in the prompts directory.
/// </summary>
/// <remarks>
/// <para>
/// Prompts live on disk rather than in C# string literals for three reasons: contributors can
/// tune controller behaviour without a build, the diff of a prompt change is readable, and the
/// content hash shown on the Diagnostics page lets a user say exactly which prompt version
/// produced a given transmission.
/// </para>
/// <para>
/// A missing prompt is reported, not thrown. StormATC falls back to deterministic phraseology,
/// so an incomplete prompt directory degrades the experience rather than breaking the session.
/// </para>
/// </remarks>
public sealed class FilePromptCatalog(
    IStoragePaths storagePaths,
    ILogger<FilePromptCatalog> logger) : IPromptCatalog
{
    private readonly IStoragePaths _storagePaths =
        storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

    private readonly ILogger<FilePromptCatalog> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _missingPrompts = [];
    private readonly Lock _missingLock = new();

    /// <inheritdoc />
    public IReadOnlyList<string> MissingPrompts
    {
        get
        {
            lock (_missingLock)
            {
                return [.. _missingPrompts];
            }
        }
    }

    /// <inheritdoc />
    public async Task<string> GetPromptAsync(string promptName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptName);

        if (_cache.TryGetValue(promptName, out string? cached))
        {
            return cached;
        }

        // An environment-specific override wins, so a contributor can experiment with a prompt
        // without editing the tracked file.
        string overridePath = Path.Combine(_storagePaths.PromptDirectory, $"{promptName}.local.md");
        string basePath = Path.Combine(_storagePaths.PromptDirectory, $"{promptName}.md");

        string path = File.Exists(overridePath) ? overridePath : basePath;

        if (!File.Exists(path))
        {
            RecordMissing(promptName);
            _logger.LogWarning(
                "Prompt '{PromptName}' was not found at {Path}. Deterministic phraseology will be used.",
                promptName,
                basePath);

            return string.Empty;
        }

        try
        {
            string content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            _cache[promptName] = content;
            _hashes[promptName] = ComputeShortHash(content);
            return content;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Prompt '{PromptName}' could not be read.", promptName);
            RecordMissing(promptName);
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public Task<bool> HasPromptAsync(string promptName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_cache.ContainsKey(promptName))
        {
            return Task.FromResult(true);
        }

        string path = Path.Combine(_storagePaths.PromptDirectory, $"{promptName}.md");
        return Task.FromResult(File.Exists(path));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetPromptHashesAsync(
        CancellationToken cancellationToken)
    {
        // Load anything not yet read so the Diagnostics page shows a complete picture.
        foreach (string name in PromptNames.All)
        {
            if (!_hashes.ContainsKey(name))
            {
                await GetPromptAsync(name, cancellationToken).ConfigureAwait(false);
            }
        }

        return new Dictionary<string, string>(_hashes, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        _cache.Clear();
        _hashes.Clear();

        lock (_missingLock)
        {
            _missingPrompts.Clear();
        }
    }

    /// <summary>
    /// A short content hash, enough to distinguish prompt versions in a diagnostics view without
    /// filling the screen with a full digest.
    /// </summary>
    private static string ComputeShortHash(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash)[..12];
    }

    private void RecordMissing(string promptName)
    {
        lock (_missingLock)
        {
            if (!_missingPrompts.Contains(promptName))
            {
                _missingPrompts.Add(promptName);
            }
        }
    }
}
