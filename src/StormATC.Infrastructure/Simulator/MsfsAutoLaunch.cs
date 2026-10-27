using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace StormATC.Infrastructure.Simulator;

/// <summary>
/// Makes MSFS start StormATC with the simulator, by keeping a <c>Launch.Addon</c> entry in the simulator's
/// <c>EXE.xml</c>. The entry is the option: it is present when StormATC starts with MSFS.
/// </summary>
/// <remarks>
/// MSFS 2024 reads that file from a different folder depending on where it was bought (Microsoft Store or Steam), and
/// a PC may hold both, so every file that exists is kept in step. The entries of other add-ons are never touched.
/// </remarks>
public static class MsfsAutoLaunch
{
    /// <summary>The name of the entry in <c>EXE.xml</c>.</summary>
    public const string EntryName = "StormATC";

    /// <summary>Where MSFS 2024 may keep its <c>EXE.xml</c>.</summary>
    public static IReadOnlyList<string> CandidateFiles()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "EXE.xml"),
            Path.Combine(roaming, "Microsoft Flight Simulator 2024", "EXE.xml"),
        ];
    }

    /// <summary>True when at least one <c>EXE.xml</c> exists, so that the option can be offered.</summary>
    public static bool IsAvailable() => CandidateFiles().Any(File.Exists);

    /// <summary>True when an enabled StormATC entry is in one of the files.</summary>
    public static bool IsEnabled() => IsEnabled(CandidateFiles());

    /// <summary>True when an enabled StormATC entry is in one of these files.</summary>
    /// <param name="files">The <c>EXE.xml</c> files.</param>
    public static bool IsEnabled(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Any(file => File.Exists(file) && TryLoad(file) is XDocument document && FindEntry(document) is { } entry
            && !string.Equals((string?)entry.Element("Disabled"), "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The path an enabled entry starts, or null.</summary>
    public static string? EnabledPath() => EnabledPath(CandidateFiles());

    /// <summary>The path an enabled entry in these files starts, or null.</summary>
    /// <param name="files">The <c>EXE.xml</c> files.</param>
    public static string? EnabledPath(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (string file in files)
        {
            if (File.Exists(file) && TryLoad(file) is XDocument document && FindEntry(document) is { } entry)
            {
                return (string?)entry.Element("Path");
            }
        }

        return null;
    }

    /// <summary>Adds or removes the StormATC entry in every <c>EXE.xml</c> that exists.</summary>
    /// <param name="enabled">True to start StormATC with MSFS.</param>
    /// <param name="executablePath">The StormATC executable.</param>
    /// <returns>How many files were changed.</returns>
    public static int Apply(bool enabled, string executablePath) => Apply(CandidateFiles(), enabled, executablePath);

    /// <summary>Adds or removes the StormATC entry in these files, those that exist.</summary>
    /// <param name="files">The <c>EXE.xml</c> files.</param>
    /// <param name="enabled">True to start StormATC with MSFS.</param>
    /// <param name="executablePath">The StormATC executable.</param>
    /// <returns>How many files were changed.</returns>
    public static int Apply(IEnumerable<string> files, bool enabled, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        int changed = 0;
        foreach (string file in files)
        {
            if (!File.Exists(file) || TryLoad(file) is not XDocument document || document.Root is null)
            {
                continue;
            }

            XElement? entry = FindEntry(document);
            bool dirty = false;

            if (enabled)
            {
                if (entry is null)
                {
                    document.Root.Add(new XElement(
                        "Launch.Addon",
                        new XElement("Name", EntryName),
                        new XElement("Disabled", "False"),
                        new XElement("ManualLoad", "False"),
                        new XElement("Path", executablePath)));
                    dirty = true;
                }
                else if (!string.Equals((string?)entry.Element("Path"), executablePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals((string?)entry.Element("Disabled"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    SetChild(entry, "Path", executablePath);
                    SetChild(entry, "Disabled", "False");
                    dirty = true;
                }
            }
            else if (entry is not null)
            {
                entry.Remove();
                dirty = true;
            }

            if (dirty)
            {
                Save(file, document);
                changed++;
            }
        }

        return changed;
    }

    private static XElement? FindEntry(XDocument document) =>
        document.Root?.Elements("Launch.Addon")
            .FirstOrDefault(addon => string.Equals((string?)addon.Element("Name"), EntryName, StringComparison.OrdinalIgnoreCase));

    private static void SetChild(XElement parent, string name, string value)
    {
        if (parent.Element(name) is { } existing)
        {
            existing.Value = value;
        }
        else
        {
            parent.Add(new XElement(name, value));
        }
    }

    private static XDocument? TryLoad(string file)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return XDocument.Load(file);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void Save(string file, XDocument document)
    {
        // The first change to a file keeps the user's own copy, in case the simulator's file was hand-edited.
        string backup = file + ".stormatc-backup";
        if (!File.Exists(backup))
        {
            File.Copy(file, backup);
        }

        // The file keeps the encoding it declares (the user's may be Windows-1252).
        Encoding encoding = document.Declaration?.Encoding is { Length: > 0 } name
            ? Encoding.GetEncoding(name)
            : new UTF8Encoding(false);
        if (encoding.CodePage == 65001)
        {
            encoding = new UTF8Encoding(false);
        }

        using var writer = XmlWriter.Create(file, new XmlWriterSettings { Encoding = encoding, Indent = true, IndentChars = "    " });
        document.Save(writer);
    }
}
