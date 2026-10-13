using System.Collections.Concurrent;
using System.Text.Json;
using StormATC.Domain.Phraseology;

namespace StormATC.Infrastructure.Speech;

/// <summary>A Piper voice together with the speaking rate chosen for it.</summary>
/// <param name="VoicePath">The <c>.onnx</c> voice model.</param>
/// <param name="LengthScale">Piper's length scale: below 1 is faster, above 1 slower.</param>
/// <param name="SpeakerId">The speaker inside a multi-speaker model, or null for single-voice models.</param>
internal sealed record ChosenVoice(string VoicePath, double LengthScale, int? SpeakerId = null);

/// <summary>
/// Finds the Piper voices available for a language and gives each controller its own.
/// </summary>
/// <remarks>
/// A controller is identified by a key such as <c>Tower@118.700</c>. The first time a key is
/// seen it is given a random voice from the pool and a speaking rate near the configured one;
/// the same key keeps that voice for the rest of the session, so Tower always sounds like
/// Tower, while a new frequency may bring a different voice (or, by chance, the same one).
/// A multi-speaker model counts as one voice in the pool, and a random speaker inside it is
/// drawn, so a 100-speaker model does not drown out the single-voice models.
/// </remarks>
internal sealed class PiperVoiceCast(Random? random = null, Func<string, int>? speakerCount = null)
{
    private const int MaximumRemembered = 64;

    private static readonly ConcurrentDictionary<string, int> SpeakerCounts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, int> _speakerCount = speakerCount ?? ReadSpeakerCount;

    /// <summary>
    /// The number of speakers in a Piper model, read from the <c>num_speakers</c> field of its
    /// <c>.onnx.json</c>. Anything unreadable counts as a single voice.
    /// </summary>
    internal static int ReadSpeakerCount(string voicePath) =>
        SpeakerCounts.GetOrAdd(voicePath, static path =>
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path + ".json"));
                return document.RootElement.TryGetProperty("num_speakers", out JsonElement count)
                    && count.TryGetInt32(out int speakers)
                        ? Math.Max(1, speakers)
                        : 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return 1;
            }
        });

    private readonly Random _random = random ?? Random.Shared;

    /// <summary>
    /// Speakers that must not be drawn, by model file name without extension. A speaker that is
    /// excluded after it was assigned is replaced the next time that controller speaks.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<int>> ExcludedSpeakers { get; set; } =
        new Dictionary<string, IReadOnlySet<int>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Splits <c>PiperExcludedVoices</c> into whole models to drop and single speakers to skip.
    /// </summary>
    /// <returns>The model names, and the excluded speaker numbers of each model.</returns>
    public static (IReadOnlyCollection<string> Models, IReadOnlyDictionary<string, IReadOnlySet<int>> Speakers)
        ParseExclusions(IEnumerable<string>? entries)
    {
        var models = new List<string>();
        var speakers = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (string entry in entries ?? [])
        {
            string trimmed = entry.Trim();
            int colon = trimmed.LastIndexOf(':');
            if (colon > 0 && int.TryParse(trimmed[(colon + 1)..], out int speaker) && speaker >= 0)
            {
                string model = trimmed[..colon].Trim();
                if (!speakers.TryGetValue(model, out HashSet<int>? set))
                {
                    speakers[model] = set = [];
                }

                set.Add(speaker);
            }
            else if (trimmed.Length > 0)
            {
                models.Add(trimmed);
            }
        }

        return (models, speakers.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<int>)pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Called when a controller is given a voice for the first time.</summary>
    public Action<string, ChosenVoice>? Assigned { get; set; }
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ChosenVoice> _assigned = new(StringComparer.Ordinal);

    /// <summary>
    /// Lists the voices to choose from: <paramref name="primaryPath"/> plus every Piper voice in
    /// <paramref name="folder"/> whose file name starts with the language code (<c>fr_</c>,
    /// <c>en_</c>) and that has its <c>.onnx.json</c> beside it.
    /// </summary>
    public static IReadOnlyList<string> Discover(
        string? folder,
        PhraseologyLanguage language,
        string? primaryPath,
        bool allowLowQuality = true,
        bool allowMultiSpeaker = true,
        IReadOnlyCollection<string>? excluded = null)
    {
        var voices = new List<string>();
        if (!string.IsNullOrWhiteSpace(primaryPath) && File.Exists(primaryPath))
        {
            voices.Add(primaryPath);
        }

        string prefix = language == PhraseologyLanguage.French ? "fr_" : "en_";
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(folder, "*.onnx").Order(StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(path + ".json")
                        && !voices.Contains(path, StringComparer.OrdinalIgnoreCase)
                        && (allowLowQuality || !name.EndsWith("-low", StringComparison.OrdinalIgnoreCase))
                        && (allowMultiSpeaker || ReadSpeakerCount(path) <= 1)
                        && excluded?.Contains(name, StringComparer.OrdinalIgnoreCase) != true)
                    {
                        voices.Add(path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder just means the primary voice is used alone.
            }
        }

        return voices;
    }

    /// <summary>Returns the voice for <paramref name="key"/>, choosing one the first time.</summary>
    /// <param name="key">The controller identity.</param>
    /// <param name="pool">The available voices; must not be empty.</param>
    /// <param name="baseLengthScale">The configured speaking rate.</param>
    /// <param name="spread">How far the rate may drift from the configured one, up or down.</param>
    public ChosenVoice Assign(string key, IReadOnlyList<string> pool, double baseLengthScale, double spread)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfZero(pool.Count);

        lock (_gate)
        {
            if (_assigned.TryGetValue(key, out ChosenVoice? existing)
                && pool.Contains(existing.VoicePath, StringComparer.OrdinalIgnoreCase)
                && !IsExcluded(existing.VoicePath, existing.SpeakerId))
            {
                return existing;
            }

            if (_assigned.Count >= MaximumRemembered)
            {
                _assigned.Clear();
            }

            double offset = spread <= 0 ? 0 : (_random.NextDouble() * 2 - 1) * spread;
            string voicePath = pool[_random.Next(pool.Count)];
            int speakers = _speakerCount(voicePath);
            var chosen = new ChosenVoice(
                voicePath,
                Math.Round(Math.Clamp(baseLengthScale + offset, 0.5, 2.0), 2),
                speakers > 1 ? DrawSpeaker(voicePath, speakers) : null);
            _assigned[key] = chosen;
            Assigned?.Invoke(key, chosen);
            return chosen;
        }
    }

    private bool IsExcluded(string voicePath, int? speaker) =>
        speaker is int number
        && ExcludedSpeakers.TryGetValue(Path.GetFileNameWithoutExtension(voicePath), out IReadOnlySet<int>? set)
        && set.Contains(number);

    /// <summary>Draws a speaker of a multi-speaker model, avoiding the excluded ones.</summary>
    private int DrawSpeaker(string voicePath, int speakers)
    {
        if (!ExcludedSpeakers.TryGetValue(Path.GetFileNameWithoutExtension(voicePath), out IReadOnlySet<int>? excluded)
            || excluded.Count == 0)
        {
            return _random.Next(speakers);
        }

        int[] allowed = [.. Enumerable.Range(0, speakers).Where(number => !excluded.Contains(number))];

        // Every speaker excluded: the model is unusable, but a controller must still speak.
        return allowed.Length == 0 ? _random.Next(speakers) : allowed[_random.Next(allowed.Length)];
    }

    /// <summary>Forgets every assignment, so the next session casts its controllers afresh.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _assigned.Clear();
        }
    }
}
