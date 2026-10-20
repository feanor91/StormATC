using StormATC.Infrastructure.Panel;

namespace StormATC.Infrastructure.Tests;

/// <summary>Installing the toolbar panel into MSFS's Community folder.</summary>
public sealed class MsfsPanelInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stormatc-install-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string MakePackage()
    {
        string package = Path.Combine(_root, "built", MsfsPanelInstaller.PackageName);
        Directory.CreateDirectory(Path.Combine(package, "html_ui", "InGamePanels", "StormATC"));
        File.WriteAllText(Path.Combine(package, "layout.json"), "{}");
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(package, "html_ui", "InGamePanels", "StormATC", "StormATC.html"), "<html/>");
        return package;
    }

    [Theory]
    [InlineData("InstalledPackagesPath \"D:\\MSFS\\Packages\"", "D:\\MSFS\\Packages")]
    [InlineData("{\r\n  Foo 1\r\n  InstalledPackagesPath \"C:\\Users\\me\\AppData\\Local\\Packages\\x\\LocalCache\\Packages\"\r\n}", "C:\\Users\\me\\AppData\\Local\\Packages\\x\\LocalCache\\Packages")]
    [InlineData("installedpackagespath \"E:\\Sim\"", "E:\\Sim")]
    public void The_packages_folder_is_read_from_the_user_configuration(string text, string expected) =>
        Assert.Equal(expected, MsfsPanelInstaller.ReadInstalledPackagesPath(text));

    [Fact]
    public void A_configuration_without_the_setting_gives_no_folder() =>
        Assert.Null(MsfsPanelInstaller.ReadInstalledPackagesPath("{ Something 1 }"));

    [Fact]
    public void The_usual_community_folders_are_the_store_and_steam_ones()
    {
        IReadOnlyList<string> folders = MsfsPanelInstaller.DefaultCommunityFolders();

        Assert.Equal(2, folders.Count);
        Assert.All(folders, folder => Assert.EndsWith(Path.Combine("Packages", "Community"), folder, StringComparison.Ordinal));
        Assert.Contains(folders, folder => folder.Contains("Microsoft.Limitless_8wekyb3d8bbwe", StringComparison.Ordinal));
    }

    [Fact]
    public void The_package_is_copied_into_the_community_folder()
    {
        string community = Path.Combine(_root, "Community");
        Directory.CreateDirectory(community);

        string installed = MsfsPanelInstaller.Install(community, MakePackage());

        Assert.Equal(Path.Combine(community, MsfsPanelInstaller.PackageName), installed);
        Assert.True(File.Exists(Path.Combine(installed, "layout.json")));
        Assert.True(File.Exists(Path.Combine(installed, "html_ui", "InGamePanels", "StormATC", "StormATC.html")));
    }

    [Fact]
    public void An_earlier_copy_is_replaced_and_other_packages_are_left_alone()
    {
        string community = Path.Combine(_root, "Community");
        string other = Path.Combine(community, "someone-elses-package");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "keep.txt"), "keep");

        string stale = Path.Combine(community, MsfsPanelInstaller.PackageName);
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "old-file.txt"), "old");

        MsfsPanelInstaller.Install(community, MakePackage());

        Assert.False(File.Exists(Path.Combine(stale, "old-file.txt")));
        Assert.True(File.Exists(Path.Combine(other, "keep.txt")));
    }

    [Fact]
    public void A_missing_community_folder_is_reported()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => MsfsPanelInstaller.Install(Path.Combine(_root, "nowhere"), MakePackage()));
    }

    [Fact]
    public void A_build_without_the_package_is_reported()
    {
        string community = Path.Combine(_root, "Community");
        Directory.CreateDirectory(community);

        Assert.Throws<FileNotFoundException>(
            () => MsfsPanelInstaller.Install(community, Path.Combine(_root, "empty")));
    }
}
