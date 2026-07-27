using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Infrastructure.Security;

namespace StormATC.Infrastructure.OpenRouter;

/// <summary>
/// Calls OpenRouter's OpenAI-compatible chat completions API.
/// </summary>
/// <remarks>
/// <para>
/// Plain <see cref="HttpClient"/> rather than a provider SDK. The API surface StormATC needs is
/// one endpoint, and avoiding an SDK keeps the dependency list honest and makes it obvious that
/// nothing but the prompt and the model name leaves the machine.
/// </para>
/// <para>
/// The API key is fetched from <see cref="ISecretStore"/> per request and attached only to the
/// Authorization header. It is never held in options, never logged, and never included in an
/// exception message — every error path here reports a category, not a raw response body that
/// might echo the header back.
/// </para>
/// </remarks>
public sealed class OpenRouterClient(
    HttpClient httpClient,
    IOptionsMonitor<OpenRouterOptions> options,
    IOptionsMonitor<ApplicationOptions> applicationOptions,
    ISecretStore secretStore,
    ILogger<OpenRouterClient> logger) : ILanguageModelClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly IOptionsMonitor<OpenRouterOptions> _options = options
        ?? throw new ArgumentNullException(nameof(options));

    private readonly IOptionsMonitor<ApplicationOptions> _applicationOptions = applicationOptions
        ?? throw new ArgumentNullException(nameof(applicationOptions));

    private readonly ISecretStore _secretStore =
        secretStore ?? throw new ArgumentNullException(nameof(secretStore));

    private readonly ILogger<OpenRouterClient> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Jitter source for retry backoff. Not used for anything security-sensitive.</summary>
    private readonly Random _jitter = new();

    /// <inheritdoc />
    public string ProviderName => "OpenRouter";

    /// <inheritdoc />
    public bool IsConfigured =>
        _applicationOptions.CurrentValue.CloudServicesConsentGiven
        && ProviderEndpointPolicy.IsAllowedOpenRouter(_options.CurrentValue.BaseUrl)
        && _options.CurrentValue.IsConfigured;

    /// <inheritdoc />
    public async Task<StructuredModelResponse<T>> GenerateStructuredAsync<T>(
        ModelRequest request,
        JsonSchemaDefinition schema,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(schema);

        var stopwatch = Stopwatch.StartNew();

        if (!_applicationOptions.CurrentValue.CloudServicesConsentGiven)
        {
            return StructuredModelResponse.Fail<T>(
                "Cloud services are disabled until consent is enabled in Settings.",
                ModelErrorCategory.NotConfigured,
                stopwatch.Elapsed);
        }

        if (!ProviderEndpointPolicy.IsAllowedOpenRouter(_options.CurrentValue.BaseUrl))
        {
            return StructuredModelResponse.Fail<T>(
                "The configured OpenRouter endpoint was blocked for security.",
                ModelErrorCategory.NotConfigured,
                stopwatch.Elapsed);
        }

        string? apiKey = await _secretStore
            .GetSecretAsync(SecretKeys.OpenRouterApiKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return StructuredModelResponse.Fail<T>(
                "No OpenRouter API key is configured.",
                ModelErrorCategory.NotConfigured,
                stopwatch.Elapsed);
        }

        string model = request.ModelOverride ?? _options.CurrentValue.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return StructuredModelResponse.Fail<T>(
                "No OpenRouter model is configured.",
                ModelErrorCategory.NotConfigured,
                stopwatch.Elapsed);
        }

        object payload = BuildPayload(request, schema, model);

        for (int attempt = 0; attempt <= _options.CurrentValue.MaximumRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (StructuredModelResponse<T> Response, bool ShouldRetry) outcome =
                await AttemptAsync<T>(payload, apiKey, request.CorrelationId, stopwatch, cancellationToken)
                    .ConfigureAwait(false);

            if (!outcome.ShouldRetry || attempt == _options.CurrentValue.MaximumRetries)
            {
                return outcome.Response;
            }

            await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
        }

        return StructuredModelResponse.Fail<T>(
            "The OpenRouter request failed after all retries.",
            ModelErrorCategory.Transient,
            stopwatch.Elapsed);
    }

    private async Task<(StructuredModelResponse<T> Response, bool ShouldRetry)> AttemptAsync<T>(
        object payload,
        string apiKey,
        string? correlationId,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (!ProviderEndpointPolicy.IsAllowedOpenRouter(_options.CurrentValue.BaseUrl))
        {
            return (
                StructuredModelResponse.Fail<T>(
                    "The configured OpenRouter endpoint was blocked for security.",
                    ModelErrorCategory.NotConfigured,
                    stopwatch.Elapsed),
                false);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.CurrentValue.Timeout);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{_options.CurrentValue.BaseUrl.TrimEnd('/')}/chat/completions"));

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        // Optional OpenRouter attribution headers. Configurable, and omitted when unset.
        if (_options.CurrentValue.RefererUrl is { Length: > 0 } referer)
        {
            message.Headers.TryAddWithoutValidation("HTTP-Referer", referer);
        }

        if (_options.CurrentValue.ApplicationTitle is { Length: > 0 } title)
        {
            message.Headers.TryAddWithoutValidation("X-Title", title);
        }

        message.Content = JsonContent.Create(payload, options: SerializerOptions);

        try
        {
            using HttpResponseMessage response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                (ModelErrorCategory category, bool retry, string description) =
                    ClassifyFailure(response.StatusCode);

                _logger.LogWarning(
                    "OpenRouter request {CorrelationId} returned {StatusCode} ({Category}).",
                    correlationId,
                    (int)response.StatusCode,
                    category);

                return (
                    StructuredModelResponse.Fail<T>(description, category, stopwatch.Elapsed),
                    retry);
            }

            // Bound the response so a pathological reply cannot exhaust memory.
            if (response.Content.Headers.ContentLength > _options.CurrentValue.MaximumResponseBytes)
            {
                return (
                    StructuredModelResponse.Fail<T>(
                        "The OpenRouter response exceeded the configured size limit.",
                        ModelErrorCategory.MalformedResponse,
                        stopwatch.Elapsed),
                    false);
            }

            string body = await response.Content.ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            if (body.Length > _options.CurrentValue.MaximumResponseBytes)
            {
                return (
                    StructuredModelResponse.Fail<T>(
                        "The OpenRouter response exceeded the configured size limit.",
                        ModelErrorCategory.MalformedResponse,
                        stopwatch.Elapsed),
                    false);
            }

            return (Deserialize<T>(body, stopwatch), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (
                StructuredModelResponse.Fail<T>(
                    "The request was cancelled.", ModelErrorCategory.Cancelled, stopwatch.Elapsed),
                false);
        }
        catch (OperationCanceledException)
        {
            return (
                StructuredModelResponse.Fail<T>(
                    $"OpenRouter did not respond within {_options.CurrentValue.Timeout.TotalSeconds:F0} seconds.",
                    ModelErrorCategory.Timeout,
                    stopwatch.Elapsed),
                true);
        }
        catch (HttpRequestException ex)
        {
            // The message is logged but never surfaced: it can contain request details.
            _logger.LogWarning(ex, "The OpenRouter request failed at the transport layer.");

            return (
                StructuredModelResponse.Fail<T>(
                    "OpenRouter could not be reached.", ModelErrorCategory.Transient, stopwatch.Elapsed),
                true);
        }
    }

    /// <summary>Extracts and deserialises the model's structured content.</summary>
    private StructuredModelResponse<T> Deserialize<T>(string body, Stopwatch stopwatch)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return StructuredModelResponse.Fail<T>(
                    "The OpenRouter response contained no completion.",
                    ModelErrorCategory.MalformedResponse,
                    stopwatch.Elapsed,
                    Truncate(body));
            }

            JsonElement firstChoice = choices[0];
            if (!firstChoice.TryGetProperty("message", out JsonElement messageElement)
                || !messageElement.TryGetProperty("content", out JsonElement contentElement)
                || contentElement.GetString() is not { Length: > 0 } content)
            {
                return StructuredModelResponse.Fail<T>(
                    "The OpenRouter completion contained no content.",
                    ModelErrorCategory.MalformedResponse,
                    stopwatch.Elapsed,
                    Truncate(body));
            }

            int? totalTokens = document.RootElement.TryGetProperty("usage", out JsonElement usage)
                && usage.TryGetProperty("total_tokens", out JsonElement tokens)
                && tokens.TryGetInt32(out int tokenCount)
                    ? tokenCount
                    : null;

            // Some models wrap JSON in a markdown fence despite the schema instruction.
            string cleaned = StripCodeFence(content);

            T? value = JsonSerializer.Deserialize<T>(cleaned, SerializerOptions);

            if (value is null)
            {
                return StructuredModelResponse.Fail<T>(
                    "The model's JSON did not match the expected schema.",
                    ModelErrorCategory.SchemaViolation,
                    stopwatch.Elapsed,
                    Truncate(cleaned));
            }

            return StructuredModelResponse.Ok(value, stopwatch.Elapsed, totalTokens);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("The OpenRouter response was not valid JSON: {Reason}", ex.Message);

            return StructuredModelResponse.Fail<T>(
                "The model returned output that is not valid JSON.",
                ModelErrorCategory.MalformedResponse,
                stopwatch.Elapsed,
                Truncate(body));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelTextDelta> StreamTextAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_applicationOptions.CurrentValue.CloudServicesConsentGiven
            || !ProviderEndpointPolicy.IsAllowedOpenRouter(_options.CurrentValue.BaseUrl))
        {
            yield break;
        }

        string? apiKey = await _secretStore
            .GetSecretAsync(SecretKeys.OpenRouterApiKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(_options.CurrentValue.Model))
        {
            yield break;
        }

        if (!ProviderEndpointPolicy.IsAllowedOpenRouter(_options.CurrentValue.BaseUrl))
        {
            yield break;
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{_options.CurrentValue.BaseUrl.TrimEnd('/')}/chat/completions"));

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(
            new
            {
                model = request.ModelOverride ?? _options.CurrentValue.Model,
                messages = BuildMessages(request),
                temperature = request.Temperature,
                max_tokens = request.MaxOutputTokens,
                stream = true,
            },
            options: SerializerOptions);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            yield break;
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(stream);

        // Read until ReadLineAsync returns null. EndOfStream would block synchronously on a
        // network stream, which is exactly what must not happen on a streaming response.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                yield break;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            string data = line["data: ".Length..];
            if (data == "[DONE]")
            {
                yield return new ModelTextDelta { Text = string.Empty, IsFinal = true };
                yield break;
            }

            string? text = TryReadDelta(data);
            if (text is { Length: > 0 })
            {
                yield return new ModelTextDelta { Text = text };
            }
        }
    }

    /// <summary>Reads one server-sent-event chunk, ignoring anything malformed.</summary>
    private static string? TryReadDelta(string data)
    {
        try
        {
            using JsonDocument chunk = JsonDocument.Parse(data);

            return chunk.RootElement.TryGetProperty("choices", out JsonElement choices)
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out JsonElement delta)
                && delta.TryGetProperty("content", out JsonElement content)
                    ? content.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // A partial chunk simply produces nothing this iteration.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!_applicationOptions.CurrentValue.CloudServicesConsentGiven)
        {
            return ProviderTestResult.Fail(
                "Cloud services are disabled. Enable consent in Settings before testing.");
        }

        if (!ProviderEndpointPolicy.IsAllowedOpenRouter(_options.CurrentValue.BaseUrl))
        {
            return ProviderTestResult.Fail(
                "The configured OpenRouter endpoint was blocked. Restore the official endpoint.");
        }

        string? apiKey = await _secretStore
            .GetSecretAsync(SecretKeys.OpenRouterApiKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderTestResult.Fail("No OpenRouter API key is stored. Enter one and save first.");
        }

        if (string.IsNullOrWhiteSpace(_options.CurrentValue.Model))
        {
            return ProviderTestResult.Fail("No OpenRouter model is configured. Enter a model ID.");
        }

        if (!ProviderEndpointPolicy.IsAllowedOpenRouter(_options.CurrentValue.BaseUrl))
        {
            return ProviderTestResult.Fail(
                "The configured OpenRouter endpoint was blocked. Restore the official endpoint.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.CurrentValue.Timeout);

        try
        {
            // Query the account endpoint rather than generating: it validates the key without
            // spending tokens on the user's behalf.
            using var message = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"{_options.CurrentValue.BaseUrl.TrimEnd('/')}/key"));

            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using HttpResponseMessage response = await _httpClient
                .SendAsync(message, timeoutSource.Token)
                .ConfigureAwait(false);

            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                return ProviderTestResult.Ok(
                    $"Connected. Model set to '{_options.CurrentValue.Model}'.", stopwatch.Elapsed);
            }

            (_, _, string description) = ClassifyFailure(response.StatusCode);
            return ProviderTestResult.Fail(description);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderTestResult.Fail("OpenRouter did not respond before the timeout elapsed.");
        }
        catch (HttpRequestException)
        {
            return ProviderTestResult.Fail("OpenRouter could not be reached. Check your connection.");
        }
    }

    private static object BuildPayload(ModelRequest request, JsonSchemaDefinition schema, string model)
    {
        using JsonDocument schemaDocument = JsonDocument.Parse(schema.Schema);

        return new
        {
            model,
            messages = BuildMessages(request),
            temperature = request.Temperature,
            max_tokens = request.MaxOutputTokens,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = schema.Name,
                    strict = schema.Strict,
                    schema = schemaDocument.RootElement.Clone(),
                },
            },
        };
    }

    private static object[] BuildMessages(ModelRequest request) =>
    [
        new { role = "system", content = request.SystemPrompt },
        new { role = "user", content = request.UserPrompt },
    ];

    /// <summary>
    /// Maps an HTTP status to an error category, whether to retry, and a user-facing message.
    /// </summary>
    private static (ModelErrorCategory Category, bool ShouldRetry, string Description) ClassifyFailure(
        HttpStatusCode statusCode) => statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (
                ModelErrorCategory.Authentication,
                false,
                "OpenRouter rejected the API key. Check it in Settings."),

            HttpStatusCode.TooManyRequests => (
                ModelErrorCategory.RateLimited,
                true,
                "OpenRouter is rate-limiting requests. StormATC will keep using deterministic phraseology."),

            HttpStatusCode.NotFound or HttpStatusCode.BadRequest => (
                ModelErrorCategory.InvalidModel,
                false,
                "OpenRouter rejected the request. The configured model may not exist or may not support structured output."),

            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => (
                ModelErrorCategory.Timeout,
                true,
                "OpenRouter timed out."),

            >= HttpStatusCode.InternalServerError => (
                ModelErrorCategory.Transient,
                true,
                "OpenRouter reported a server error."),

            _ => (
                ModelErrorCategory.Unknown,
                false,
                $"OpenRouter returned HTTP {(int)statusCode}."),
        };

    /// <summary>
    /// Waits before a retry, using exponential backoff with jitter.
    /// </summary>
    /// <remarks>
    /// Jitter matters even for a single-user desktop application: without it, a rate-limited
    /// client retries on a fixed schedule and tends to collide with its own next attempt.
    /// </remarks>
    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        double baseDelayMs = 500 * Math.Pow(2, attempt);
        double jitterMs = _jitter.Next(0, 250);

        await Task.Delay(TimeSpan.FromMilliseconds(baseDelayMs + jitterMs), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Removes a markdown code fence some models wrap JSON in.</summary>
    private static string StripCodeFence(string content)
    {
        string trimmed = content.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed;
        }

        string body = trimmed[(firstNewline + 1)..];
        int closingFence = body.LastIndexOf("```", StringComparison.Ordinal);

        return closingFence >= 0 ? body[..closingFence].Trim() : body.Trim();
    }

    /// <summary>Truncates a response for diagnostics so a huge body cannot fill the log.</summary>
    private static string Truncate(string value) =>
        value.Length <= 2000 ? value : value[..2000] + "… (truncated)";
}
