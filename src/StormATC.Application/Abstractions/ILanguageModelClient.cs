namespace StormATC.Application.Abstractions;

/// <summary>
/// A chat-completion provider capable of returning schema-constrained JSON.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately provider-neutral. OpenRouter is the first implementation, but nothing in this
/// interface is OpenRouter-shaped, so a local model or a different gateway can be added without
/// touching the application layer.
/// </para>
/// <para>
/// Every result from this interface is untrusted. Callers must validate against the schema, then
/// against the domain, before anything reaches the ATC engine.
/// </para>
/// </remarks>
public interface ILanguageModelClient
{
    /// <summary>A stable identifier for this client, shown on the Diagnostics page.</summary>
    string ProviderName { get; }

    /// <summary>True when the client has the configuration it needs to make a request.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Requests a completion constrained to a JSON schema and deserialises it.
    /// </summary>
    /// <typeparam name="T">The expected response shape.</typeparam>
    /// <param name="request">Prompt and generation parameters.</param>
    /// <param name="schema">The schema the provider must constrain output to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StructuredModelResponse<T>> GenerateStructuredAsync<T>(
        ModelRequest request,
        JsonSchemaDefinition schema,
        CancellationToken cancellationToken);

    /// <summary>Streams a plain-text completion.</summary>
    IAsyncEnumerable<ModelTextDelta> StreamTextAsync(
        ModelRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Verifies credentials and model availability without changing any session state.
    /// Backs the "Test connection" button in Settings.
    /// </summary>
    Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>A request to a language model.</summary>
public sealed record ModelRequest
{
    /// <summary>The system prompt, loaded from the prompt catalog.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>The user message.</summary>
    public required string UserPrompt { get; init; }

    /// <summary>
    /// Sampling temperature. Intent parsing uses 0 for determinism; phraseology uses a small
    /// value so repeated instructions do not sound identical.
    /// </summary>
    public double Temperature { get; init; }

    /// <summary>Maximum tokens to generate.</summary>
    public int MaxOutputTokens { get; init; } = 512;

    /// <summary>Correlation identifier, carried into logs and the diagnostics view.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Overrides the configured model for this request only.</summary>
    public string? ModelOverride { get; init; }
}

/// <summary>The outcome of a structured generation request.</summary>
/// <typeparam name="T">The expected response shape.</typeparam>
public sealed record StructuredModelResponse<T>
{
    /// <summary>Whether a valid, schema-conforming response was received.</summary>
    public required bool Success { get; init; }

    /// <summary>The deserialised value, when the request succeeded.</summary>
    public T? Value { get; init; }

    /// <summary>The raw response text, kept for diagnostics when parsing fails.</summary>
    public string? RawResponse { get; init; }

    /// <summary>Why the request failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The failure category, which decides whether a retry makes sense.</summary>
    public ModelErrorCategory ErrorCategory { get; init; }

    /// <summary>How long the request took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Tokens consumed, where the provider reports them.</summary>
    public int? TotalTokens { get; init; }
}

/// <summary>
/// Factory methods for <see cref="StructuredModelResponse{T}"/>.
/// </summary>
/// <remarks>
/// Non-generic so that callers write <c>StructuredModelResponse.Ok(value, duration)</c> and let
/// the compiler infer the type argument, rather than repeating it.
/// </remarks>
public static class StructuredModelResponse
{
    /// <summary>Builds a success result.</summary>
    public static StructuredModelResponse<T> Ok<T>(T value, TimeSpan duration, int? tokens = null) => new()
    {
        Success = true,
        Value = value,
        Duration = duration,
        TotalTokens = tokens,
    };

    /// <summary>Builds a failure result.</summary>
    public static StructuredModelResponse<T> Fail<T>(
        string message,
        ModelErrorCategory category,
        TimeSpan duration,
        string? rawResponse = null) => new()
        {
            Success = false,
            ErrorMessage = message,
            ErrorCategory = category,
            Duration = duration,
            RawResponse = rawResponse,
        };
}

/// <summary>A chunk of streamed text.</summary>
public sealed record ModelTextDelta
{
    /// <summary>The text fragment.</summary>
    public required string Text { get; init; }

    /// <summary>True when this is the final chunk.</summary>
    public bool IsFinal { get; init; }
}

/// <summary>Why a model request failed. Drives retry behaviour and the message shown to the user.</summary>
public enum ModelErrorCategory
{
    None = 0,

    /// <summary>No API key is configured.</summary>
    NotConfigured,

    /// <summary>The key was rejected.</summary>
    Authentication,

    /// <summary>The provider rate-limited the request. Retryable after a delay.</summary>
    RateLimited,

    /// <summary>The request timed out. Retryable.</summary>
    Timeout,

    /// <summary>A transient network or 5xx failure. Retryable.</summary>
    Transient,

    /// <summary>The configured model does not exist or is unavailable to this account.</summary>
    InvalidModel,

    /// <summary>The provider returned something that is not valid JSON.</summary>
    MalformedResponse,

    /// <summary>The response was valid JSON but did not match the schema.</summary>
    SchemaViolation,

    /// <summary>The response was rejected by domain validation — an impossible aviation value.</summary>
    DomainValidationFailure,

    /// <summary>The caller cancelled the request.</summary>
    Cancelled,

    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>A JSON schema, supplied to the provider to constrain output.</summary>
public sealed record JsonSchemaDefinition
{
    /// <summary>The schema name sent to the provider.</summary>
    public required string Name { get; init; }

    /// <summary>The schema document, as a JSON string.</summary>
    public required string Schema { get; init; }

    /// <summary>
    /// When true, the provider must reject any output that does not conform, rather than
    /// making a best effort.
    /// </summary>
    public bool Strict { get; init; } = true;
}

/// <summary>The outcome of a provider connection test.</summary>
public sealed record ProviderTestResult
{
    /// <summary>Whether the provider responded successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>A short message shown next to the Test button.</summary>
    public required string Message { get; init; }

    /// <summary>Round-trip time.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Builds a success result.</summary>
    public static ProviderTestResult Ok(string message, TimeSpan duration) =>
        new() { Success = true, Message = message, Duration = duration };

    /// <summary>Builds a failure result.</summary>
    public static ProviderTestResult Fail(string message) =>
        new() { Success = false, Message = message };
}
