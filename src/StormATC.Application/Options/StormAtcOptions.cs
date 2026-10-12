using System.ComponentModel.DataAnnotations;

namespace StormATC.Application.Options;

/// <summary>General application behaviour.</summary>
/// <remarks>
/// Configuration precedence, lowest to highest: <c>appsettings.json</c>,
/// <c>appsettings.{Environment}.json</c>, user settings, environment variables prefixed
/// <c>STORMATC_</c>, then command-line arguments. API keys are never read from any of these —
/// they live only in the OS secret store. See <c>docs/development.md</c>.
/// </remarks>
public sealed class ApplicationOptions
{
    public const string SectionName = "Application";

    /// <summary>
    /// When true, StormATC makes no network requests at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a hard switch, not a preference: with it on, the language model and speech
    /// services are replaced by implementations that cannot reach the network, and SimBrief is
    /// not contacted even when a user ID is configured. The simulator connection is unaffected —
    /// SimConnect is a local inter-process link.
    /// </para>
    /// <para>
    /// It defaults to <see langword="false"/> because it is not what makes StormATC private.
    /// Every outbound provider is chosen on its own configuration and stays inert until the user
    /// supplies a key or an account number, so a default installation contacts nothing either
    /// way. This exists for people who want that guaranteed rather than merely true.
    /// </para>
    /// </remarks>
    public bool OfflineMode { get; init; }

    /// <summary>Enables the developer tools: manual text input and the mock simulator controls.</summary>
    public bool DeveloperMode { get; init; }

    /// <summary>True once the user has accepted the first-run cloud-services consent screen.</summary>
    public bool CloudServicesConsentGiven { get; init; }

    /// <summary>True once the user has completed first-run setup.</summary>
    public bool FirstRunCompleted { get; init; }

    /// <summary>
    /// Realism profile name: <c>Relaxed</c>, <c>Standard</c>, or <c>Strict</c>.
    /// </summary>
    public string RealismProfile { get; init; } = "Standard";

    /// <summary>
    /// The language controllers speak and pilots are expected to use: <c>en</c> (ICAO English,
    /// the default) or <c>fr</c> (French, DSNA phraseology).
    /// </summary>
    /// <remarks>
    /// The language changes wording, speech recognition and the controller voice only. Every
    /// clearance decision is made by the same deterministic engine in either language.
    /// </remarks>
    public string AtcLanguage { get; init; } = "en";
}

/// <summary>Language-model selection and generation settings shared by every provider.</summary>
public sealed class LanguageModelOptions
{
    public const string SectionName = "LanguageModel";

    /// <summary>Selected provider: OpenRouter, OpenAI, Groq, DeepSeek, or Anthropic.</summary>
    public string Provider { get; init; } = LanguageModelProviders.OpenRouter;

    /// <summary>Temperature for intent parsing. Zero, because parsing must be reproducible.</summary>
    [Range(0.0, 2.0)]
    public double IntentParserTemperature { get; init; }

    /// <summary>
    /// Temperature for phraseology. Zero keeps standard phraseology stable; users can opt into
    /// a small amount of variation without changing the approved facts.
    /// </summary>
    [Range(0.0, 2.0)]
    public double PhraseologyTemperature { get; init; }
}

/// <summary>Stable language-model provider identifiers used by configuration and the UI.</summary>
public static class LanguageModelProviders
{
    public const string OpenRouter = "OpenRouter";
    public const string OpenAI = "OpenAI";
    public const string Groq = "Groq";
    public const string DeepSeek = "DeepSeek";
    public const string Anthropic = "Anthropic";

    public static IReadOnlyList<string> All { get; } =
        [OpenRouter, OpenAI, Groq, DeepSeek, Anthropic];

    public static string Normalize(string? provider) =>
        All.FirstOrDefault(item => item.Equals(provider, StringComparison.OrdinalIgnoreCase))
        ?? OpenRouter;
}

/// <summary>OpenRouter connection settings.</summary>
/// <remarks>
/// The API key is deliberately absent. It is held in <see cref="Abstractions.ISecretStore"/>
/// so that it cannot be serialised into a settings file, written to a log, or included in a
/// diagnostics export.
/// </remarks>
public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    /// <summary>API base URL.</summary>
    [Url]
    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1";

    /// <summary>
    /// The model identifier, for example <c>anthropic/claude-sonnet-4.5</c>. Deliberately not
    /// defaulted to a specific model: StormATC is provider-flexible and the user chooses.
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Request timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How many times to retry a transient failure.</summary>
    [Range(0, 5)]
    public int MaximumRetries { get; init; } = 2;

    /// <summary>Maximum response bytes accepted, as a defence against a runaway response.</summary>
    [Range(1024, 10 * 1024 * 1024)]
    public int MaximumResponseBytes { get; init; } = 256 * 1024;

    /// <summary>Site URL sent in the optional OpenRouter attribution header.</summary>
    public string? RefererUrl { get; init; } = "https://github.com/stormatc/stormatc";

    /// <summary>Application name sent in the optional OpenRouter attribution header.</summary>
    public string? ApplicationTitle { get; init; } = "StormATC";

    /// <summary>True when enough is configured to attempt a request.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Model);
}

/// <summary>OpenAI API connection settings. A ChatGPT subscription is not an API credential.</summary>
public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    [Url]
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";

    public string Model { get; init; } = string.Empty;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    [Range(0, 5)]
    public int MaximumRetries { get; init; } = 2;

    [Range(1024, 10 * 1024 * 1024)]
    public int MaximumResponseBytes { get; init; } = 256 * 1024;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Model);
}

/// <summary>Groq API connection settings.</summary>
public sealed class GroqOptions
{
    public const string SectionName = "Groq";

    [Url]
    public string BaseUrl { get; init; } = "https://api.groq.com/openai/v1";

    public string Model { get; init; } = string.Empty;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    [Range(0, 5)]
    public int MaximumRetries { get; init; } = 2;

    [Range(1024, 10 * 1024 * 1024)]
    public int MaximumResponseBytes { get; init; } = 256 * 1024;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Model);
}

/// <summary>DeepSeek API connection settings.</summary>
public sealed class DeepSeekOptions
{
    public const string SectionName = "DeepSeek";

    [Url]
    public string BaseUrl { get; init; } = "https://api.deepseek.com";

    public string Model { get; init; } = string.Empty;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    [Range(0, 5)]
    public int MaximumRetries { get; init; } = 2;

    [Range(1024, 10 * 1024 * 1024)]
    public int MaximumResponseBytes { get; init; } = 256 * 1024;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Model);
}

/// <summary>Anthropic Messages API connection settings.</summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    [Url]
    public string BaseUrl { get; init; } = "https://api.anthropic.com";

    public string Model { get; init; } = string.Empty;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    [Range(0, 5)]
    public int MaximumRetries { get; init; } = 2;

    [Range(1024, 10 * 1024 * 1024)]
    public int MaximumResponseBytes { get; init; } = 256 * 1024;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Model);
}

/// <summary>
/// Local speech engines that run on the user's computer: Whisper (whisper.cpp) for recognition
/// and Piper for the controller voice. Neither needs an account or a network connection.
/// </summary>
/// <remarks>
/// Both engines are external programs the user downloads once (see
/// <c>docs/french-and-local-voice.md</c>). StormATC starts them per transmission and never
/// writes microphone audio anywhere except a temporary file deleted after transcription.
/// </remarks>
public sealed class LocalVoiceOptions
{
    public const string SectionName = "LocalVoice";

    /// <summary>Speech-to-text engine: <c>Deepgram</c> (cloud) or <c>Whisper</c> (local).</summary>
    public string SpeechToTextProvider { get; init; } = SpeechProviders.Deepgram;

    /// <summary>Controller voice engine: <c>Deepgram</c> (cloud) or <c>Piper</c> (local).</summary>
    public string TextToSpeechProvider { get; init; } = SpeechProviders.Deepgram;

    /// <summary>Path to whisper.cpp's <c>whisper-cli.exe</c>.</summary>
    public string WhisperExecutablePath { get; init; } = string.Empty;

    /// <summary>
    /// Path to a multilingual ggml Whisper model, for example <c>ggml-large-v3-turbo-q5_0.bin</c>
    /// or <c>ggml-small.bin</c>. English-only models (<c>*.en.bin</c>) cannot recognise French.
    /// </summary>
    public string WhisperModelPath { get; init; } = string.Empty;

    /// <summary>CPU threads given to Whisper.</summary>
    [Range(1, 64)]
    public int WhisperThreads { get; init; } = 4;

    /// <summary>Longest time allowed for one transcription before it is abandoned.</summary>
    public TimeSpan WhisperTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Path to Piper's <c>piper.exe</c>.</summary>
    public string PiperExecutablePath { get; init; } = string.Empty;

    /// <summary>Piper voice model (<c>.onnx</c>) used for English, for example <c>en_GB-alan-medium.onnx</c>.</summary>
    public string PiperEnglishVoicePath { get; init; } = string.Empty;

    /// <summary>Piper voice model (<c>.onnx</c>) used for French, for example <c>fr_FR-tom-medium.onnx</c>.</summary>
    public string PiperFrenchVoicePath { get; init; } = string.Empty;

    /// <summary>
    /// Piper speaking rate: below 1 is faster, above 1 slower. Controllers speak briskly, so the
    /// default is slightly faster than the voice's natural pace.
    /// </summary>
    [Range(0.5, 2.0)]
    public double PiperLengthScale { get; init; } = 0.9;

    /// <summary>Longest time allowed for one synthesis before it is abandoned.</summary>
    public TimeSpan PiperTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>Stable speech engine identifiers used by configuration and the UI.</summary>
public static class SpeechProviders
{
    /// <summary>Deepgram cloud speech (recognition and voice).</summary>
    public const string Deepgram = "Deepgram";

    /// <summary>whisper.cpp local recognition.</summary>
    public const string Whisper = "Whisper";

    /// <summary>Piper local voice.</summary>
    public const string Piper = "Piper";

    /// <summary>Recognition engines offered in Settings.</summary>
    public static IReadOnlyList<string> RecognitionEngines { get; } = [Deepgram, Whisper];

    /// <summary>Voice engines offered in Settings.</summary>
    public static IReadOnlyList<string> VoiceEngines { get; } = [Deepgram, Piper];

    /// <summary>True when <paramref name="value"/> names <paramref name="provider"/>, ignoring case.</summary>
    public static bool Is(string? value, string provider) =>
        string.Equals(value?.Trim(), provider, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Deepgram speech settings.</summary>
public sealed class DeepgramOptions
{
    public const string SectionName = "Deepgram";

    /// <summary>Base URL for the REST API.</summary>
    [Url]
    public string BaseUrl { get; init; } = "https://api.deepgram.com";

    /// <summary>WebSocket URL for streaming speech-to-text.</summary>
    public string StreamingUrl { get; init; } = "wss://api.deepgram.com/v1/listen";

    /// <summary>Speech-to-text model identifier.</summary>
    public string SpeechToTextModel { get; init; } = "nova-3";

    /// <summary>Text-to-speech voice identifier.</summary>
    public string Voice { get; init; } = "aura-2-thalia-en";

    /// <summary>Text-to-speech voice used when the ATC language is French.</summary>
    public string FrenchVoice { get; init; } = "aura-2-hector-fr";

    /// <summary>Recognition language.</summary>
    public string Language { get; init; } = "en";

    /// <summary>Capture sample rate in hertz.</summary>
    [Range(8000, 48000)]
    public int SampleRate { get; init; } = 16000;

    /// <summary>
    /// Longest single transmission accepted. Bounds cost and stops a stuck PTT key from
    /// streaming indefinitely.
    /// </summary>
    public TimeSpan MaximumTransmissionDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the streaming connection before failing.</summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Input device name, or empty for the system default.</summary>
    public string? InputDeviceName { get; init; }

    /// <summary>Output device name, or empty for the system default.</summary>
    public string? OutputDeviceName { get; init; }

    /// <summary>
    /// Personal phrases supplied to Deepgram as Nova-3 keyterms.
    /// </summary>
    public IReadOnlyList<string> RecognitionKeyterms { get; init; } = [];

    /// <summary>
    /// Explicit transcript corrections learned by the guided voice trainer.
    /// </summary>
    public IReadOnlyDictionary<string, string> TranscriptCorrections { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

}

/// <summary>SimBrief import settings.</summary>
public sealed class SimBriefOptions
{
    public const string SectionName = "SimBrief";

    /// <summary>The OFP fetch endpoint.</summary>
    [Url]
    public string BaseUrl { get; init; } = "https://www.simbrief.com/api/xml.fetcher.php";

    /// <summary>
    /// The SimBrief user identifier. Not a secret — it is an account number, not a credential —
    /// so unlike API keys it lives in ordinary settings.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>Request timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How old an imported plan may be before the UI warns that it is stale.</summary>
    public TimeSpan StaleThreshold { get; init; } = TimeSpan.FromHours(12);

    /// <summary>When true, the raw XML is cached locally for diagnostics.</summary>
    public bool CacheRawResponses { get; init; }
}

/// <summary>Simulator connection settings.</summary>
public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    /// <summary>
    /// Preferred connector: <c>MSFS</c> or <c>Mock</c>.
    /// </summary>
    /// <remarks>
    /// MSFS is the default. When the SDK is missing, or the simulator simply is not running,
    /// StormATC falls back to the scripted mock aircraft, says so, and keeps watching for the
    /// simulator to appear. <c>Mock</c> means never contact SimConnect at all.
    /// </remarks>
    public string PreferredConnector { get; init; } = "MSFS";

    /// <summary>
    /// Aircraft state updates per second. Five is enough for the engine's decisions while
    /// leaving the UI thread idle most of the time.
    /// </summary>
    [Range(1, 30)]
    public int UpdatesPerSecond { get; init; } = 5;

    /// <summary>Delay between reconnection attempts.</summary>
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>When true, reconnect automatically after the simulator restarts.</summary>
    public bool AutoReconnect { get; init; } = true;
}

/// <summary>Optional live-traffic awareness and background radio simulation.</summary>
/// <remarks>
/// Every feature in this section defaults off. Reading SimConnect traffic is local and free,
/// but language-model wording and synthesized voices can consume third-party API credits.
/// Requiring separate opt-ins prevents adding an API key from silently enabling recurring calls.
/// </remarks>
public sealed class TrafficOptions
{
    public const string SectionName = "Traffic";

    /// <summary>
    /// Reads nearby simulator-managed aircraft, including FSLTL and VATSIM model-matched traffic.
    /// </summary>
    public bool EnableLiveTraffic { get; init; }

    /// <summary>Produces occasional controller-and-pilot exchanges for observed traffic.</summary>
    public bool EnableRadioChatter { get; init; }

    /// <summary>
    /// Allows one constrained language-model request per generated exchange for natural wording.
    /// When false, the local deterministic phrase set is used and no model credits are consumed.
    /// </summary>
    public bool UseLanguageModelForChatter { get; init; }

    /// <summary>
    /// Synthesizes both sides of a generated exchange through the configured TTS provider.
    /// When false, exchanges remain text-only and consume no speech credits.
    /// </summary>
    public bool SpeakChatter { get; init; }

    /// <summary>
    /// Suppresses generated radio while a common online-ATC client is running.
    /// </summary>
    public bool PauseChatterWhenOnlineAtcClientDetected { get; init; } = true;

    /// <summary>Hard hourly ceiling for generated exchanges and their associated API calls.</summary>
    [Range(1, 30)]
    public int MaximumExchangesPerHour { get; init; } = 6;

    /// <summary>Minimum quiet time between background exchanges.</summary>
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>Audio capture, playback, and radio-effect settings.</summary>
public sealed class AudioOptions
{
    public const string SectionName = "Audio";

    /// <summary>Controller voice volume, 0 to 1.</summary>
    [Range(0.0, 1.0)]
    public double ControllerVolume { get; init; } = 0.85;

    /// <summary>When true, the radio effect is applied to controller audio.</summary>
    public bool RadioEffectEnabled { get; init; } = true;

    /// <summary>Radio-effect intensity, 0 to 1.</summary>
    [Range(0.0, 1.0)]
    public double RadioEffectStrength { get; init; } = 0.5;

    /// <summary>Background static level, 0 to 1.</summary>
    [Range(0.0, 1.0)]
    public double StaticLevel { get; init; } = 0.08;

    /// <summary>When true, transmission-start and -end clicks are played.</summary>
    public bool TransmissionClicksEnabled { get; init; } = true;

    /// <summary>Mutes controller audio without changing the volume setting.</summary>
    public bool Muted { get; init; }

    /// <summary>
    /// Bypasses the radio effect entirely. Provided for accessibility and for troubleshooting
    /// intelligibility problems.
    /// </summary>
    public bool CleanAudioMode { get; init; }

    /// <summary>The push-to-talk key name.</summary>
    public string PushToTalkKey { get; init; } = "Space";

    /// <summary>When true, the assigned joystick button also controls push-to-talk.</summary>
    public bool JoystickPushToTalkEnabled { get; init; }

    /// <summary>The last Windows joystick ID used by the assigned controller.</summary>
    [Range(0, 31)]
    public int? JoystickDeviceId { get; init; }

    /// <summary>
    /// The assigned controller name. Used to recover the binding when Windows changes its ID.
    /// </summary>
    public string? JoystickDeviceName { get; init; }

    /// <summary>The zero-based joystick button index assigned to push-to-talk.</summary>
    [Range(0, 31)]
    public int? JoystickButtonIndex { get; init; }
}

/// <summary>Persistence and retention settings.</summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>When true, transcripts are written to the local database.</summary>
    public bool StoreTranscripts { get; init; } = true;

    /// <summary>How long sessions are kept before the retention sweep removes them.</summary>
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(30);

    /// <summary>When true, old sessions are deleted automatically at startup.</summary>
    public bool AutomaticRetentionSweep { get; init; } = true;
}

/// <summary>ATC realism and behaviour settings.</summary>
public sealed class AtcRealismOptions
{
    public const string SectionName = "AtcRealism";

    /// <summary>
    /// Relative GSX-style tail direction used when the pilot did not request one explicitly.
    /// </summary>
    public string DefaultPushbackDirection { get; init; } = "tail left";

    /// <summary>When true, an outstanding mandatory readback blocks further clearances.</summary>
    public bool RequireReadbackBeforeProgressing { get; init; } = true;

    /// <summary>When true, a transmission using the wrong callsign is refused.</summary>
    public bool RequireCorrectCallsign { get; init; } = true;

    /// <summary>When true, crossing a hold-short line without clearance raises a warning.</summary>
    public bool EnforceHoldShort { get; init; } = true;

    /// <summary>When true, the pilot must check in after every frequency change.</summary>
    public bool RequireCheckInOnFrequencyChange { get; init; }

    /// <summary>Below this confidence the engine asks the pilot to repeat.</summary>
    [Range(0.0, 1.0)]
    public double MinimumIntentConfidence { get; init; } = 0.6;

    /// <summary>Below this confidence the intent parser escalates to the language model.</summary>
    [Range(0.0, 1.0)]
    public double LanguageModelEscalationThreshold { get; init; } = 0.7;

    /// <summary>
    /// When true, the language model is allowed to phrase approved responses. When false,
    /// StormATC uses deterministic phraseology only and makes no phraseology model calls.
    /// </summary>
    public bool UseLanguageModelPhraseology { get; init; } = true;
}

/// <summary>Diagnostics and logging settings.</summary>
public sealed class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    /// <summary>When true, per-stage latency is recorded and shown on the Diagnostics page.</summary>
    public bool RecordLatencyMetrics { get; init; } = true;

    /// <summary>How many recent latency samples to keep in memory.</summary>
    [Range(10, 1000)]
    public int LatencySampleCount { get; init; } = 100;

    /// <summary>How many days of rolling log files to keep.</summary>
    [Range(1, 90)]
    public int LogRetentionDays { get; init; } = 7;

    /// <summary>When true, prompt content hashes are shown on the Diagnostics page.</summary>
    public bool ShowPromptHashes { get; init; } = true;
}
