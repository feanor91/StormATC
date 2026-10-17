using System.Text.RegularExpressions;

namespace StormATC.Infrastructure.Panel;

/// <summary>
/// Copies the StormATC toolbar panel into MSFS's Community folder, so that it appears in the
/// simulator's toolbar.
/// </summary>
/// <remarks>
/// MSFS 2024 keeps the path of its package folders in <c>UserCfg.opt</c>, which differs between
/// the Microsoft Store and the Steam editions. Only a copy of the panel's own folder is made
/// (or replaced); nothing else in the Community folder is touched.
/// </remarks>
public static partial class MsfsPanelInstaller
{
    /// <summary>The package's folder name, in the Community folder and in the application.</summary>
    public const string PackageName = "stormatc-ingamepanel";

    /// <summary>The folder inside the application that holds the built package.</summary>
    public static string SourceFolder =>
        Path.Combine(AppContext.BaseDirectory, "msfs-panel", PackageName);

    /// <summary>Where MSFS 2024 may keep its <c>UserCfg.opt</c>.</summary>
    public static IReadOnlyList<string> UserConfigurationFiles()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return
        [
            Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
            Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt"),
        ];
    }

    /// <summary>The Community folders of the installed simulators, as far as they can be found.</summary>
    public static IReadOnlyList<string> FindCommunityFolders()
    {
        List<string> found = [];
        foreach (string file in UserConfigurationFiles())
        {
            if (!File.Exists(file))
            {
                continue;
            }

            string? packages = ReadInstalledPackagesPath(File.ReadAllText(file));
            if (packages is null)
            {
                continue;
            }

            string community = Path.Combine(packages, "Community");
            if (Directory.Exists(community) && !found.Contains(community, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(community);
            }
        }

        return found;
    }

    /// <summary>Reads the <c>InstalledPackagesPath</c> setting from the text of a <c>UserCfg.opt</c>.</summary>
    /// <param name="userConfiguration">The file's text.</param>
    /// <returns>The path, or null when the file does not give one.</returns>
    public static string? ReadInstalledPackagesPath(string userConfiguration)
    {
        ArgumentNullException.ThrowIfNull(userConfiguration);

        Match match = InstalledPackagesPathPattern().Match(userConfiguration);
        return match.Success ? match.Groups["path"].Value.Trim() : null;
    }

    /// <summary>Copies the package into a Community folder, replacing an earlier copy.</summary>
    /// <param name="communityFolder">The Community folder.</param>
    /// <param name="sourceFolder">The built package; the application's own copy when null.</param>
    /// <returns>The folder the panel was installed to.</returns>
    public static string Install(string communityFolder, string? sourceFolder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(communityFolder);

        string source = sourceFolder ?? SourceFolder;
        if (!File.Exists(Path.Combine(source, "layout.json")))
        {
            throw new FileNotFoundException(
                "The MSFS panel package is not part of this build of StormATC.",
                Path.Combine(source, "layout.json"));
        }

        if (!Directory.Exists(communityFolder))
        {
            throw new DirectoryNotFoundException($"The Community folder {communityFolder} does not exist.");
        }

        string destination = Path.Combine(communityFolder, PackageName);
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        CopyDirectory(source, destination);
        return destination;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    [GeneratedRegex("""InstalledPackagesPath\s+"(?<path>[^"]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstalledPackagesPathPattern();
}
