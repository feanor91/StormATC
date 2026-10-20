using System.Text.Json;
using System.Text.Json.Nodes;

namespace StormATC.Infrastructure.LocalVoice;

/// <summary>
/// Points the local voice settings at the Whisper, Piper and voice files the installer put in the
/// standard folder, so that a fresh installation works without anyone typing a path.
/// </summary>
/// <remarks>
/// <para>
/// The installer downloads whisper.cpp, a Whisper model, Piper and a set of voices into
/// <c>%LOCALAPPDATA%\StormATC\local-voice</c>. On start, this fills in the <c>LocalVoice</c>
/// section of <c>usersettings.json</c>, and only the parts that are not already set: a setting
/// the user chose, and a path that still points at a file that exists, are left alone. A path
/// that points at a file that is gone (a reinstalled PC) is replaced.
/// </para>
/// <para>
/// The engines themselves (<c>SpeechToTextProvider</c> and <c>TextToSpeechProvider</c>) are only
/// switched on when they have never been set, so a user who chose the cloud engines keeps them.
/// </para>
/// </remarks>
public static class LocalVoiceBootstrap
{
    private const string Section = "LocalVoice";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>The folder the installer downloads the local voice files to.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StormATC",
        "local-voice");

    /// <summary>Fills in the settings from the files found under <paramref name="root"/>.</summary>
    /// <param name="userSettingsPath">The path of <c>usersettings.json</c>.</param>
    /// <param name="root">The folder holding <c>piper</c>, <c>whisper</c>, <c>models</c> and <c>voices</c>.</param>
    /// <returns>True when the settings file was changed.</returns>
    public static bool Apply(string userSettingsPath, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSettingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            return false;
        }

        string piper = Path.Combine(root, "piper", "piper.exe");
        string whisper = Path.Combine(root, "whisper", "bin", "Release", "whisper-cli.exe");
        string model = Path.Combine(root, "models", "ggml-small-q5_1.bin");
        string voices = Path.Combine(root, "voices");

        JsonObject settings;
        try
        {
            settings = File.Exists(userSettingsPath)
                ? JsonNode.Parse(File.ReadAllText(userSettingsPath)) as JsonObject ?? []
                : [];
        }
        catch (JsonException)
        {
            // A settings file that cannot be read is the user's to fix; it is not overwritten.
            return false;
        }

        if (settings[Section] is not JsonObject voice)
        {
            voice = [];
            settings[Section] = voice;
        }

        bool changed = false;

        void SetPath(string key, string path)
        {
            if (File.Exists(path) && NeedsPath(voice, key))
            {
                voice[key] = path;
                changed = true;
            }
        }

        SetPath("WhisperExecutablePath", whisper);
        SetPath("WhisperModelPath", model);
        SetPath("PiperExecutablePath", piper);

        if (Directory.Exists(voices))
        {
            string[] models = Directory.GetFiles(voices, "*.onnx");
            if (models.Length > 0 && NeedsPath(voice, "PiperVoicesFolder", isFolder: true))
            {
                voice["PiperVoicesFolder"] = voices;
                changed = true;
            }

            if (PickVoice(models, "fr_", "fr_FR-tom-medium", "fr_FR-siwis-medium") is { } french)
            {
                SetPath("PiperFrenchVoicePath", french);
            }

            if (PickVoice(models, "en_", "en_GB-alan-medium", "en_US-ryan-medium") is { } english)
            {
                SetPath("PiperEnglishVoicePath", english);
            }
        }

        // The engines are switched on only when the files exist and nothing was ever chosen.
        if (File.Exists(whisper) && File.Exists(model) && IsUnset(voice, "SpeechToTextProvider"))
        {
            voice["SpeechToTextProvider"] = "Whisper";
            changed = true;
        }

        if (File.Exists(piper) && IsUnset(voice, "TextToSpeechProvider"))
        {
            voice["TextToSpeechProvider"] = "Piper";
            changed = true;
        }

        if (changed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(userSettingsPath)!);
            File.WriteAllText(userSettingsPath, settings.ToJsonString(WriteOptions));
        }

        return changed;
    }

    private static bool IsUnset(JsonObject section, string key) =>
        section[key] is null || string.IsNullOrWhiteSpace(section[key]!.GetValue<string?>());

    /// <summary>A path setting needs filling in when it is empty or names something that is gone.</summary>
    private static bool NeedsPath(JsonObject section, string key, bool isFolder = false)
    {
        if (IsUnset(section, key))
        {
            return true;
        }

        string current = section[key]!.GetValue<string>().Trim().Trim('"');
        return isFolder ? !Directory.Exists(current) : !File.Exists(current);
    }

    private static string? PickVoice(string[] models, string prefix, params string[] preferred)
    {
        foreach (string name in preferred)
        {
            string? match = models.FirstOrDefault(
                path => string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return models
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileNameWithoutExtension(path).EndsWith("-low", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
