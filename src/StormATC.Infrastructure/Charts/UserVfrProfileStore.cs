using System.Text.Json;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.Charts;
using StormATC.Infrastructure.Data;

namespace StormATC.Infrastructure.Charts;

/// <summary>
/// Keeps the VFR profiles the pilot confirmed in <c>%LOCALAPPDATA%\StormATC\vfr-profiles</c>, a folder
/// that is read before the profiles bundled with StormATC and that an update never touches.
/// </summary>
public sealed class UserVfrProfileStore(IStoragePaths storagePaths, ILogger<UserVfrProfileStore> logger) : IVacProfileStore
{
    /// <summary>The name of the folder, under the application data folder and beside the bundled data.</summary>
    public const string FolderName = "vfr-profiles";

    /// <summary>The pilot's folder.</summary>
    public string UserDirectory => Path.Combine(storagePaths.ApplicationDataDirectory, FolderName);

    /// <inheritdoc />
    public VacProfileOrigin Origin(string icao)
    {
        if (!IsIcao(icao))
        {
            return VacProfileOrigin.None;
        }

        if (File.Exists(UserPath(icao)))
        {
            return VacProfileOrigin.User;
        }

        return File.Exists(BundledPath(icao)) ? VacProfileOrigin.Bundled : VacProfileOrigin.None;
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(string icao, string profileJson, CancellationToken cancellationToken)
    {
        if (!IsIcao(icao))
        {
            throw new ArgumentException("An aerodrome's ICAO code has four letters.", nameof(icao));
        }

        // What is written must be a profile StormATC reads back.
        using (JsonDocument document = JsonDocument.Parse(profileJson))
        {
            VfrAerodromeProfileCheck.RequireReadable(document.RootElement, icao);
        }

        string path = UserPath(icao);
        Directory.CreateDirectory(UserDirectory);

        // A profile the pilot saved before is kept beside the new one.
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        await File.WriteAllTextAsync(path, profileJson, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Saved the VFR profile of {Icao} in {Path}.", icao.ToUpperInvariant(), path);
        return path;
    }

    private string UserPath(string icao) => Path.Combine(UserDirectory, icao.ToUpperInvariant() + ".json");

    private string BundledPath(string icao) =>
        Path.Combine(storagePaths.DataDirectory, FolderName, icao.ToUpperInvariant() + ".json");

    private static bool IsIcao(string icao) =>
        icao is { Length: 4 } && icao.All(char.IsAsciiLetter);
}

/// <summary>Checks that a profile document is one the engine can read.</summary>
internal static class VfrAerodromeProfileCheck
{
    /// <summary>Throws when the document is not a readable profile for the aerodrome.</summary>
    /// <param name="root">The document.</param>
    /// <param name="icao">The aerodrome it must be for.</param>
    public static void RequireReadable(JsonElement root, string icao)
    {
        var profile = JsonVfrProfileProvider.Parse(root)
            ?? throw new InvalidOperationException("The profile is not readable.");
        if (!string.Equals(profile.Icao, icao, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The profile is for another aerodrome.");
        }
    }
}
