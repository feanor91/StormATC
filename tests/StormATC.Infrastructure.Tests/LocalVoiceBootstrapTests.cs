using System.Text.Json.Nodes;
using StormATC.Infrastructure.LocalVoice;

namespace StormATC.Infrastructure.Tests;

/// <summary>Wiring the settings to the files the installer downloaded.</summary>
public sealed class LocalVoiceBootstrapTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "stormatc-bootstrap-" + Guid.NewGuid().ToString("N"));

    private string Root => Path.Combine(_folder, "local-voice");

    private string Settings => Path.Combine(_folder, "usersettings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private void Touch(params string[] relativePaths)
    {
        foreach (string relative in relativePaths)
        {
            string path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    private void InstallEverything() => Touch(
        @"piper\piper.exe",
        @"whisper\bin\Release\whisper-cli.exe",
        @"models\ggml-small-q5_1.bin",
        @"voices\fr_FR-tom-medium.onnx",
        @"voices\fr_FR-siwis-medium.onnx",
        @"voices\en_GB-alan-medium.onnx");

    private JsonObject Voice() => (JsonObject)JsonNode.Parse(File.ReadAllText(Settings))!["LocalVoice"]!;

    [Fact]
    public void A_fresh_installation_is_wired_to_the_downloaded_files()
    {
        InstallEverything();

        bool changed = LocalVoiceBootstrap.Apply(Settings, Root);

        Assert.True(changed);
        JsonObject voice = Voice();
        Assert.Equal("Whisper", (string?)voice["SpeechToTextProvider"]);
        Assert.Equal("Piper", (string?)voice["TextToSpeechProvider"]);
        Assert.Equal(Path.Combine(Root, "whisper", "bin", "Release", "whisper-cli.exe"), (string?)voice["WhisperExecutablePath"]);
        Assert.Equal(Path.Combine(Root, "models", "ggml-small-q5_1.bin"), (string?)voice["WhisperModelPath"]);
        Assert.Equal(Path.Combine(Root, "piper", "piper.exe"), (string?)voice["PiperExecutablePath"]);
        Assert.Equal(Path.Combine(Root, "voices"), (string?)voice["PiperVoicesFolder"]);
        Assert.Equal(Path.Combine(Root, "voices", "fr_FR-tom-medium.onnx"), (string?)voice["PiperFrenchVoicePath"]);
        Assert.Equal(Path.Combine(Root, "voices", "en_GB-alan-medium.onnx"), (string?)voice["PiperEnglishVoicePath"]);
    }

    [Fact]
    public void Nothing_is_written_when_nothing_was_downloaded()
    {
        Assert.False(LocalVoiceBootstrap.Apply(Settings, Root));
        Directory.CreateDirectory(Root);
        Assert.False(LocalVoiceBootstrap.Apply(Settings, Root));
        Assert.False(File.Exists(Settings));
    }

    [Fact]
    public void The_users_own_choices_are_kept()
    {
        InstallEverything();
        string mine = Path.Combine(_folder, "my-voice.onnx");
        File.WriteAllText(mine, "x");
        File.WriteAllText(Settings, new JsonObject
        {
            ["LocalVoice"] = new JsonObject
            {
                ["TextToSpeechProvider"] = "Deepgram",
                ["PiperFrenchVoicePath"] = mine,
            },
            ["Audio"] = new JsonObject { ["Volume"] = 7 },
        }.ToJsonString());

        LocalVoiceBootstrap.Apply(Settings, Root);

        JsonObject voice = Voice();
        Assert.Equal("Deepgram", (string?)voice["TextToSpeechProvider"]);
        Assert.Equal(mine, (string?)voice["PiperFrenchVoicePath"]);
        Assert.Equal("Whisper", (string?)voice["SpeechToTextProvider"]);
        Assert.Equal(7, (int)JsonNode.Parse(File.ReadAllText(Settings))!["Audio"]!["Volume"]!);
    }

    [Fact]
    public void A_path_to_a_file_that_is_gone_is_replaced()
    {
        InstallEverything();
        File.WriteAllText(Settings, new JsonObject
        {
            ["LocalVoice"] = new JsonObject { ["PiperExecutablePath"] = @"C:\StormATC\piper\piper.exe-gone" },
        }.ToJsonString());

        LocalVoiceBootstrap.Apply(Settings, Root);

        Assert.Equal(Path.Combine(Root, "piper", "piper.exe"), (string?)Voice()["PiperExecutablePath"]);
    }

    [Fact]
    public void A_second_start_changes_nothing()
    {
        InstallEverything();
        LocalVoiceBootstrap.Apply(Settings, Root);
        string first = File.ReadAllText(Settings);

        Assert.False(LocalVoiceBootstrap.Apply(Settings, Root));
        Assert.Equal(first, File.ReadAllText(Settings));
    }

    [Fact]
    public void Another_french_voice_is_used_when_the_preferred_ones_are_missing()
    {
        Touch(@"piper\piper.exe", @"voices\fr_FR-upmc-medium.onnx", @"voices\fr_FR-gilles-low.onnx");

        LocalVoiceBootstrap.Apply(Settings, Root);

        Assert.Equal(Path.Combine(Root, "voices", "fr_FR-upmc-medium.onnx"), (string?)Voice()["PiperFrenchVoicePath"]);
    }

    [Fact]
    public void An_unreadable_settings_file_is_not_overwritten()
    {
        InstallEverything();
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Settings, "{ not json");

        Assert.False(LocalVoiceBootstrap.Apply(Settings, Root));
        Assert.Equal("{ not json", File.ReadAllText(Settings));
    }
}
