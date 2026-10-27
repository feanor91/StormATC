using System.Text;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Infrastructure.Tests;

/// <summary>The entry StormATC keeps in the EXE.xml of MSFS.</summary>
public sealed class MsfsAutoLaunchTests : IDisposable
{
    private const string Exe = @"C:\Users\pilot\AppData\Local\Programs\StormATC\StormATC.exe";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "stormatc-exe-" + Guid.NewGuid().ToString("N"));

    public MsfsAutoLaunchTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, string encoding = "utf-8")
    {
        string path = Path.Combine(_folder, name);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllText(
            path,
            $"""
            <?xml version="1.0" encoding="{encoding}"?>
            <SimBase.Document Type="Launch" version="1,0">
                <Descr>Launch</Descr>
                <Launch.Addon>
                    <Name>Other add-on é</Name>
                    <Disabled>False</Disabled>
                    <Path>C:\Other\other.exe</Path>
                </Launch.Addon>
            </SimBase.Document>
            """,
            Encoding.GetEncoding(encoding));
        return path;
    }

    [Fact]
    public void Enabling_adds_one_entry_and_keeps_the_others_and_a_backup()
    {
        string file = Write("exe.xml");

        Assert.False(MsfsAutoLaunch.IsEnabled([file]));
        Assert.Equal(1, MsfsAutoLaunch.Apply([file], true, Exe));
        Assert.Equal(0, MsfsAutoLaunch.Apply([file], true, Exe));

        Assert.True(MsfsAutoLaunch.IsEnabled([file]));
        Assert.Equal(Exe, MsfsAutoLaunch.EnabledPath([file]));
        string text = File.ReadAllText(file);
        Assert.Contains("Other add-on é", text, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "<Name>StormATC</Name>"));
        Assert.True(File.Exists(file + ".stormatc-backup"));
    }

    [Fact]
    public void Disabling_removes_only_the_stormatc_entry()
    {
        string file = Write("exe.xml");
        MsfsAutoLaunch.Apply([file], true, Exe);

        Assert.Equal(1, MsfsAutoLaunch.Apply([file], false, Exe));

        Assert.False(MsfsAutoLaunch.IsEnabled([file]));
        Assert.Contains("other.exe", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void A_moved_program_updates_the_path_and_a_windows_1252_file_keeps_its_encoding()
    {
        string file = Write("exe1252.xml", "windows-1252");
        MsfsAutoLaunch.Apply([file], true, @"C:\Old\StormATC.exe");

        Assert.Equal(1, MsfsAutoLaunch.Apply([file], true, Exe));

        Assert.Equal(Exe, MsfsAutoLaunch.EnabledPath([file]));
        Assert.Contains("windows-1252", File.ReadAllText(file, Encoding.Latin1), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("é", File.ReadAllText(file, Encoding.GetEncoding(1252)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_or_broken_file_is_left_alone()
    {
        string broken = Path.Combine(_folder, "broken.xml");
        File.WriteAllText(broken, "not xml");

        Assert.Equal(0, MsfsAutoLaunch.Apply([Path.Combine(_folder, "none.xml"), broken], true, Exe));
        Assert.Equal("not xml", File.ReadAllText(broken));
    }
}
