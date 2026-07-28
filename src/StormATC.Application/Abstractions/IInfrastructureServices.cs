using StormATC.Domain;
using StormATC.Domain.Sessions;

namespace StormATC.Application.Abstractions;

/// <summary>
/// Stores API keys using an operating-system-provided user-scoped protection mechanism.
/// </summary>
/// <remarks>
/// Keys are never written to <c>appsettings.json</c>, never persisted to SQLite, and never
/// logged. On Windows the implementation uses DPAPI, which ties the ciphertext to the current
/// user account. See <c>docs/privacy-and-api-keys.md</c>.
/// </remarks>
public interface ISecretStore
{
    /// <summary>Retrieves a secret, or null when it has not been stored.</summary>
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stores a secret. Called only in response to an explicit user action.</summary>
    Task SetSecretAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>Deletes a secret.</summary>
    Task RemoveSecretAsync(string key, CancellationToken cancellationToken);

    /// <summary>True when a secret exists, without retrieving its value.</summary>
    Task<bool> HasSecretAsync(string key, CancellationToken cancellationToken);
}

/// <summary>The well-known secret-store keys.</summary>
public static class SecretKeys
{
    /// <summary>The OpenRouter API key.</summary>
    public const string OpenRouterApiKey = "openrouter.apiKey";

    /// <summary>The OpenAI API key.</summary>
    public const string OpenAIApiKey = "openai.apiKey";

    /// <summary>The Groq API key.</summary>
    public const string GroqApiKey = "groq.apiKey";

    /// <summary>The DeepSeek API key.</summary>
    public const string DeepSeekApiKey = "deepseek.apiKey";

    /// <summary>The Anthropic API key.</summary>
    public const string AnthropicApiKey = "anthropic.apiKey";

    /// <summary>The Deepgram API key.</summary>
    public const string DeepgramApiKey = "deepgram.apiKey";
}

/// <summary>
/// Resolves the writable locations StormATC uses, so tests never touch the real user profile.
/// </summary>
public interface IStoragePaths
{
    /// <summary>Root application-data directory.</summary>
    string ApplicationDataDirectory { get; }

    /// <summary>Full path to the SQLite database.</summary>
    string DatabasePath { get; }

    /// <summary>Directory for rolling log files.</summary>
    string LogDirectory { get; }

    /// <summary>Directory for cached raw flight-plan downloads.</summary>
    string FlightPlanCacheDirectory { get; }

    /// <summary>Directory containing the runtime-loaded prompt files.</summary>
    string PromptDirectory { get; }

    /// <summary>Directory containing bundled sample data.</summary>
    string DataDirectory { get; }

    /// <summary>Creates any directory that does not yet exist.</summary>
    void EnsureCreated();
}

/// <summary>
/// Supplies the current time. Injected so the engine and coordinator can be tested against a
/// fixed clock rather than wall time.
/// </summary>
public interface IClock
{
    /// <summary>The current instant.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>The default clock, reading the system time.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Loads controller prompts from disk, so behaviour can be edited without rebuilding.
/// </summary>
public interface IPromptCatalog
{
    /// <summary>Loads a prompt by name, for example <c>40-tower-controller</c>.</summary>
    Task<string> GetPromptAsync(string promptName, CancellationToken cancellationToken);

    /// <summary>True when a prompt exists.</summary>
    Task<bool> HasPromptAsync(string promptName, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a short content hash per prompt, shown on the Diagnostics page so a user can
    /// tell which prompt version produced a given response.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetPromptHashesAsync(CancellationToken cancellationToken);

    /// <summary>Names of prompts the catalog expected but could not find.</summary>
    IReadOnlyList<string> MissingPrompts { get; }

    /// <summary>Drops the cache so edited prompt files are picked up.</summary>
    void Invalidate();
}

/// <summary>The well-known prompt names.</summary>
public static class PromptNames
{
    public const string GlobalControllerRules = "00-global-controller-rules";
    public const string PilotIntentParser = "10-pilot-intent-parser";
    public const string ClearanceDelivery = "20-clearance-delivery";
    public const string GroundController = "30-ground-controller";
    public const string TowerController = "40-tower-controller";
    public const string DepartureController = "50-departure-controller";
    public const string TrafficChatter = "55-traffic-chatter";
    public const string CenterController = "60-center-controller";
    public const string ApproachController = "70-approach-controller";
    public const string ReadbackEvaluator = "80-readback-evaluator";
    public const string PhraseologyRenderer = "90-phraseology-renderer";
    public const string AbnormalRequests = "99-abnormal-requests";

    /// <summary>Every prompt the application expects to find.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        GlobalControllerRules, PilotIntentParser, ClearanceDelivery, GroundController,
        TowerController, DepartureController, TrafficChatter, CenterController, ApproachController,
        ReadbackEvaluator, PhraseologyRenderer, AbnormalRequests,
    ];

    /// <summary>The prompt that governs a given controller position.</summary>
    public static string ForController(ControllerPosition position) => position switch
    {
        ControllerPosition.ClearanceDelivery => ClearanceDelivery,
        ControllerPosition.Ground => GroundController,
        ControllerPosition.Tower => TowerController,
        ControllerPosition.Departure => DepartureController,
        ControllerPosition.Center => CenterController,
        ControllerPosition.Approach => ApproachController,
        _ => GlobalControllerRules,
    };
}

/// <summary>Persists sessions, conversation turns, and decisions.</summary>
public interface ISessionRepository
{
    /// <summary>Creates the schema if it does not exist and applies any pending upgrade.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or updates a session record.</summary>
    Task SaveSessionAsync(AtcSession session, CancellationToken cancellationToken);

    /// <summary>Appends a conversation turn.</summary>
    Task SaveConversationTurnAsync(Guid sessionId, ConversationTurn turn, CancellationToken cancellationToken);

    /// <summary>Records an ATC decision for diagnostics.</summary>
    Task SaveDecisionAsync(Guid sessionId, PersistedDecision decision, CancellationToken cancellationToken);

    /// <summary>Records a clearance.</summary>
    Task SaveClearanceAsync(Guid sessionId, Clearance clearance, CancellationToken cancellationToken);

    /// <summary>Records a controller handoff.</summary>
    Task SaveHandoffAsync(Guid sessionId, ControllerHandoff handoff, CancellationToken cancellationToken);

    /// <summary>Records an application-level event.</summary>
    Task SaveApplicationEventAsync(PersistedApplicationEvent applicationEvent, CancellationToken cancellationToken);

    /// <summary>Lists recent sessions, newest first.</summary>
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Loads the conversation for a session, oldest first.</summary>
    Task<IReadOnlyList<ConversationTurn>> LoadConversationAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Deletes all locally stored history. Backs the "Clear local history" action.</summary>
    Task ClearAllHistoryAsync(CancellationToken cancellationToken);

    /// <summary>The current schema version, shown on the Diagnostics page.</summary>
    Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken);
}

/// <summary>A stored ATC decision.</summary>
public sealed record PersistedDecision
{
    public required Guid DecisionId { get; init; }
    public required Guid TransmissionId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required bool Accepted { get; init; }
    public required string ResponseType { get; init; }
    public string? RejectionReason { get; init; }
    public string? ReadbackOutcome { get; init; }
    public required string CorrelationId { get; init; }
    public bool UsedDeterministicFallback { get; init; }
    public string? ValidationFailureReason { get; init; }
}

/// <summary>A stored application event, such as a provider failure.</summary>
public sealed record PersistedApplicationEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public Guid? SessionId { get; init; }
    public string? CorrelationId { get; init; }
    public AtcSeverity Severity { get; init; } = AtcSeverity.Information;
}

/// <summary>A row in the session list.</summary>
public sealed record SessionSummary
{
    public required Guid SessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Callsign { get; init; }
    public string? Departure { get; init; }
    public string? Destination { get; init; }
    public required string Mode { get; init; }
    public int TurnCount { get; init; }
}
