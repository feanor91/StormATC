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
/// Routes StormATC's provider-neutral model requests to OpenRouter, OpenAI, Groq, DeepSeek, or
/// Anthropic. The historical class name is retained to avoid a breaking internal rename.
/// </summary>
/// <remarks>
/// API keys are fetched from the operating-system secret store for each request. Every target is
/// pinned to its provider's official HTTPS endpoint and redirects are disabled by registration.
/// </remarks>
public sealed class OpenRouterClient(
    HttpClient httpClient,
    IOptionsMonitor<LanguageModelOptions> languageModelOptions,
    IOptionsMonitor<OpenRouterOptions> openRouterOptions,
    IOptionsMonitor<OpenAIOptions> openAIOptions,
    IOptionsMonitor<GroqOptions> groqOptions,
    IOptionsMonitor<DeepSeekOptions> deepSeekOptions,
    IOptionsMonitor<AnthropicOptions> anthropicOptions,
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
    private readonly IOptionsMonitor<LanguageModelOptions> _languageModelOptions =
        languageModelOptions ?? throw new ArgumentNullException(nameof(languageModelOptions));
    private readonly IOptionsMonitor<OpenRouterOptions> _openRouterOptions =
        openRouterOptions ?? throw new ArgumentNullException(nameof(openRouterOptions));
    private readonly IOptionsMonitor<OpenAIOptions> _openAIOptions =
        openAIOptions ?? throw new ArgumentNullException(nameof(openAIOptions));
    private readonly IOptionsMonitor<GroqOptions> _groqOptions =
        groqOptions ?? throw new ArgumentNullException(nameof(groqOptions));
    private readonly IOptionsMonitor<DeepSeekOptions> _deepSeekOptions =
        deepSeekOptions ?? throw new ArgumentNullException(nameof(deepSeekOptions));
    private readonly IOptionsMonitor<AnthropicOptions> _anthropicOptions =
        anthropicOptions ?? throw new ArgumentNullException(nameof(anthropicOptions));
    private readonly IOptionsMonitor<ApplicationOptions> _applicationOptions =
        applicationOptions ?? throw new ArgumentNullException(nameof(applicationOptions));
    private readonly ISecretStore _secretStore =
        secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    private readonly ILogger<OpenRouterClient> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly Random _jitter = new();

    /// <inheritdoc />
    public string ProviderName => ResolveProvider().Name;

    /// <inheritdoc />
    public bool IsConfigured
    {
        get
        {
            ProviderConfiguration provider = ResolveProvider();
            return _applicationOptions.CurrentValue.CloudServicesConsentGiven
                && provider.EndpointAllowed
                && !string.IsNullOrWhiteSpace(provider.Model);
        }
    }

    /// <inheritdoc />
    public async Task<StructuredModelResponse<T>> GenerateStructuredAsync<T>(
        ModelRequest request,
        JsonSchemaDefinition schema,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(schema);

        var stopwatch = Stopwatch.StartNew();
        ProviderConfiguration provider = ResolveProvider();

        if (!_applicationOptions.CurrentValue.CloudServicesConsentGiven)
        {
            return StructuredModelResponse.Fail<T>(
                "Cloud services are disabled until consent is enabled in Settings.",
                ModelErrorCategory.NotConfigured,
                stopwatch.Elapsed);
        }

        if (!provider.EndpointAllowed)
        {
            return StructuredModelResponse.Fail<T>(
                $"The configured {provider.Name} endpoint was blocked for security.",
                ModelErrorCategory.NotConfigured,
                stopwatch.Elapsed);
        }

        string? apiKey = await _secretStore
            .GetSecretAsync(provider.SecretKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return StructuredModelResponse.Fail<T>(
                $"No {provider.Name} API key is configured.",
                ModelErrorCategory.NotConfigured,
                stopwatch.Elapsed);
        }

        string model = request.ModelOverride ?? provider.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return StructuredModelResponse.Fail<T>(
                $"No {provider.Name} model is configured.",
                ModelErrorCategory.NotConfigured,
                stopwatch.Elapsed);
        }

        object payload = BuildStructuredPayload(request, schema, model, provider.Protocol);

        for (int attempt = 0; attempt <= provider.MaximumRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (StructuredModelResponse<T> Response, bool ShouldRetry) outcome =
                await AttemptAsync<T>(
                        payload,
                        apiKey,
                        provider,
                        request.CorrelationId,
                        stopwatch,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!outcome.ShouldRetry || attempt == provider.MaximumRetries)
            {
                return outcome.Response;
            }

            await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
        }

        return StructuredModelResponse.Fail<T>(
            $"{provider.Name} failed after all retries.",
            ModelErrorCategory.Transient,
            stopwatch.Elapsed);
    }

    private async Task<(StructuredModelResponse<T> Response, bool ShouldRetry)> AttemptAsync<T>(
        object payload,
        string apiKey,
        ProviderConfiguration provider,
        string? correlationId,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (!provider.EndpointAllowed)
        {
            return (
                StructuredModelResponse.Fail<T>(
                    $"The configured {provider.Name} endpoint was blocked for security.",
                    ModelErrorCategory.NotConfigured,
                    stopwatch.Elapsed),
                false);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(provider.Timeout);
        using HttpRequestMessage message = CreateCompletionRequest(provider, apiKey);
        message.Content = JsonContent.Create(payload, options: SerializerOptions);

        try
        {
            using HttpResponseMessage response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                (ModelErrorCategory category, bool retry, string description) =
                    ClassifyFailure(response.StatusCode, provider.Name);

                _logger.LogWarning(
                    "{Provider} request {CorrelationId} returned {StatusCode} ({Category}).",
                    provider.Name,
                    correlationId,
                    (int)response.StatusCode,
                    category);

                return (
                    StructuredModelResponse.Fail<T>(description, category, stopwatch.Elapsed),
                    retry);
            }

            if (response.Content.Headers.ContentLength > provider.MaximumResponseBytes)
            {
                return (
                    StructuredModelResponse.Fail<T>(
                        $"{provider.Name} returned an oversized response.",
                        ModelErrorCategory.MalformedResponse,
                        stopwatch.Elapsed),
                    false);
            }

            string body = await response.Content.ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            if (body.Length > provider.MaximumResponseBytes)
            {
                return (
                    StructuredModelResponse.Fail<T>(
                        $"{provider.Name} returned an oversized response.",
                        ModelErrorCategory.MalformedResponse,
                        stopwatch.Elapsed),
                    false);
            }

            return (Deserialize<T>(body, provider, stopwatch), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (
                StructuredModelResponse.Fail<T>(
                    "The request was cancelled.",
                    ModelErrorCategory.Cancelled,
                    stopwatch.Elapsed),
                false);
        }
        catch (OperationCanceledException)
        {
            return (
                StructuredModelResponse.Fail<T>(
                    $"{provider.Name} did not respond within {provider.Timeout.TotalSeconds:F0} seconds.",
                    ModelErrorCategory.Timeout,
                    stopwatch.Elapsed),
                true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "{Provider} failed at the transport layer.", provider.Name);
            return (
                StructuredModelResponse.Fail<T>(
                    $"{provider.Name} could not be reached.",
                    ModelErrorCategory.Transient,
                    stopwatch.Elapsed),
                true);
        }
    }

    private StructuredModelResponse<T> Deserialize<T>(
        string body,
        ProviderConfiguration provider,
        Stopwatch stopwatch)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            string? content;
            int? totalTokens;

            if (provider.Protocol == ProviderProtocol.Anthropic)
            {
                content = ReadAnthropicContent(document.RootElement);
                totalTokens = ReadAnthropicTokens(document.RootElement);
            }
            else
            {
                content = ReadOpenAIContent(document.RootElement);
                totalTokens = document.RootElement.TryGetProperty("usage", out JsonElement usage)
                    && usage.TryGetProperty("total_tokens", out JsonElement tokens)
                    && tokens.TryGetInt32(out int tokenCount)
                        ? tokenCount
                        : null;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return StructuredModelResponse.Fail<T>(
                    $"{provider.Name} returned no completion content.",
                    ModelErrorCategory.MalformedResponse,
                    stopwatch.Elapsed,
                    Truncate(body));
            }

            string cleaned = StripCodeFence(content);
            T? value = JsonSerializer.Deserialize<T>(cleaned, SerializerOptions);
            return value is null
                ? StructuredModelResponse.Fail<T>(
                    "The model's JSON did not match the expected schema.",
                    ModelErrorCategory.SchemaViolation,
                    stopwatch.Elapsed,
                    Truncate(cleaned))
                : StructuredModelResponse.Ok(value, stopwatch.Elapsed, totalTokens);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                "{Provider} response was not valid JSON: {Reason}",
                provider.Name,
                ex.Message);

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

        ProviderConfiguration provider = ResolveProvider();
        if (!_applicationOptions.CurrentValue.CloudServicesConsentGiven
            || !provider.EndpointAllowed
            || string.IsNullOrWhiteSpace(provider.Model))
        {
            yield break;
        }

        string? apiKey = await _secretStore
            .GetSecretAsync(provider.SecretKey, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield break;
        }

        using HttpRequestMessage message = CreateCompletionRequest(provider, apiKey);
        message.Content = JsonContent.Create(
            BuildStreamingPayload(request, request.ModelOverride ?? provider.Model, provider.Protocol),
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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield return new ModelTextDelta { Text = string.Empty, IsFinal = true };
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

            string? text = provider.Protocol == ProviderProtocol.Anthropic
                ? TryReadAnthropicDelta(data)
                : TryReadOpenAIDelta(data);
            if (text is { Length: > 0 })
            {
                yield return new ModelTextDelta { Text = text };
            }
        }
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        ProviderConfiguration provider = ResolveProvider();

        if (!_applicationOptions.CurrentValue.CloudServicesConsentGiven)
        {
            return ProviderTestResult.Fail(
                "Cloud services are disabled. Enable consent in Settings before testing.");
        }

        if (!provider.EndpointAllowed)
        {
            return ProviderTestResult.Fail(
                $"The configured {provider.Name} endpoint was blocked. Restore the official endpoint.");
        }

        string? apiKey = await _secretStore
            .GetSecretAsync(provider.SecretKey, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderTestResult.Fail(
                $"No {provider.Name} API key is stored. Enter one and save first.");
        }

        if (string.IsNullOrWhiteSpace(provider.Model))
        {
            return ProviderTestResult.Fail(
                $"No {provider.Name} model is configured. Enter a model ID and save settings.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(provider.Timeout);

        try
        {
            string path = provider.Protocol == ProviderProtocol.OpenRouter ? "key"
                : provider.Protocol == ProviderProtocol.Anthropic ? "v1/models"
                : "models";
            using var message = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"{provider.BaseUrl.TrimEnd('/')}/{path}"));
            ApplyAuthentication(message, provider, apiKey);

            using HttpResponseMessage response = await _httpClient
                .SendAsync(message, timeoutSource.Token)
                .ConfigureAwait(false);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                return ProviderTestResult.Ok(
                    $"Connected to {provider.Name}. Model set to '{provider.Model}'.",
                    stopwatch.Elapsed);
            }

            (_, _, string description) = ClassifyFailure(response.StatusCode, provider.Name);
            return ProviderTestResult.Fail(description);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderTestResult.Fail(
                $"{provider.Name} did not respond before the timeout elapsed.");
        }
        catch (HttpRequestException)
        {
            return ProviderTestResult.Fail(
                $"{provider.Name} could not be reached. Check your connection.");
        }
    }

    private static HttpRequestMessage CreateCompletionRequest(
        ProviderConfiguration provider,
        string apiKey)
    {
        string path = provider.Protocol == ProviderProtocol.Anthropic
            ? "v1/messages"
            : "chat/completions";
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{provider.BaseUrl.TrimEnd('/')}/{path}"));
        ApplyAuthentication(message, provider, apiKey);

        if (provider.Protocol == ProviderProtocol.OpenRouter)
        {
            if (provider.RefererUrl is { Length: > 0 } referer)
            {
                message.Headers.TryAddWithoutValidation("HTTP-Referer", referer);
            }

            if (provider.ApplicationTitle is { Length: > 0 } title)
            {
                message.Headers.TryAddWithoutValidation("X-Title", title);
            }
        }

        return message;
    }

    private static void ApplyAuthentication(
        HttpRequestMessage message,
        ProviderConfiguration provider,
        string apiKey)
    {
        if (provider.Protocol == ProviderProtocol.Anthropic)
        {
            message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            return;
        }

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static object BuildStructuredPayload(
        ModelRequest request,
        JsonSchemaDefinition schema,
        string model,
        ProviderProtocol protocol)
    {
        using JsonDocument schemaDocument = JsonDocument.Parse(schema.Schema);
        JsonElement schemaElement = schemaDocument.RootElement.Clone();

        if (protocol == ProviderProtocol.Anthropic)
        {
            return new
            {
                model,
                system = request.SystemPrompt,
                messages = new[] { new { role = "user", content = request.UserPrompt } },
                temperature = request.Temperature,
                max_tokens = request.MaxOutputTokens,
                output_config = new
                {
                    format = new { type = "json_schema", schema = schemaElement },
                },
            };
        }

        if (protocol == ProviderProtocol.DeepSeek)
        {
            string schemaInstruction =
                $"{request.SystemPrompt}\n\nReturn JSON only. It must match this JSON Schema exactly:\n"
                + schema.Schema;
            return new
            {
                model,
                messages = BuildMessages(schemaInstruction, request.UserPrompt),
                temperature = request.Temperature,
                max_tokens = request.MaxOutputTokens,
                response_format = new { type = "json_object" },
            };
        }

        object responseFormat = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = schema.Name,
                strict = schema.Strict,
                schema = schemaElement,
            },
        };

        return protocol == ProviderProtocol.OpenAI
            ? new
            {
                model,
                messages = BuildMessages(request.SystemPrompt, request.UserPrompt),
                temperature = request.Temperature,
                max_completion_tokens = request.MaxOutputTokens,
                response_format = responseFormat,
            }
            : new
            {
                model,
                messages = BuildMessages(request.SystemPrompt, request.UserPrompt),
                temperature = request.Temperature,
                max_tokens = request.MaxOutputTokens,
                response_format = responseFormat,
            };
    }

    private static object BuildStreamingPayload(
        ModelRequest request,
        string model,
        ProviderProtocol protocol)
    {
        if (protocol == ProviderProtocol.Anthropic)
        {
            return new
            {
                model,
                system = request.SystemPrompt,
                messages = new[] { new { role = "user", content = request.UserPrompt } },
                temperature = request.Temperature,
                max_tokens = request.MaxOutputTokens,
                stream = true,
            };
        }

        return new
        {
            model,
            messages = BuildMessages(request.SystemPrompt, request.UserPrompt),
            temperature = request.Temperature,
            max_tokens = request.MaxOutputTokens,
            stream = true,
        };
    }

    private static object[] BuildMessages(string systemPrompt, string userPrompt) =>
    [
        new { role = "system", content = systemPrompt },
        new { role = "user", content = userPrompt },
    ];

    private ProviderConfiguration ResolveProvider()
    {
        string selected = LanguageModelProviders.Normalize(
            _languageModelOptions.CurrentValue.Provider);

        if (selected == LanguageModelProviders.OpenAI)
        {
            OpenAIOptions options = _openAIOptions.CurrentValue;
            return new(
                selected, options.BaseUrl, options.Model, options.Timeout,
                options.MaximumRetries, options.MaximumResponseBytes,
                SecretKeys.OpenAIApiKey, ProviderProtocol.OpenAI,
                ProviderEndpointPolicy.IsAllowedOpenAI(options.BaseUrl));
        }

        if (selected == LanguageModelProviders.Groq)
        {
            GroqOptions options = _groqOptions.CurrentValue;
            return new(
                selected, options.BaseUrl, options.Model, options.Timeout,
                options.MaximumRetries, options.MaximumResponseBytes,
                SecretKeys.GroqApiKey, ProviderProtocol.Groq,
                ProviderEndpointPolicy.IsAllowedGroq(options.BaseUrl));
        }

        if (selected == LanguageModelProviders.DeepSeek)
        {
            DeepSeekOptions options = _deepSeekOptions.CurrentValue;
            return new(
                selected, options.BaseUrl, options.Model, options.Timeout,
                options.MaximumRetries, options.MaximumResponseBytes,
                SecretKeys.DeepSeekApiKey, ProviderProtocol.DeepSeek,
                ProviderEndpointPolicy.IsAllowedDeepSeek(options.BaseUrl));
        }

        if (selected == LanguageModelProviders.Anthropic)
        {
            AnthropicOptions options = _anthropicOptions.CurrentValue;
            return new(
                selected, options.BaseUrl, options.Model, options.Timeout,
                options.MaximumRetries, options.MaximumResponseBytes,
                SecretKeys.AnthropicApiKey, ProviderProtocol.Anthropic,
                ProviderEndpointPolicy.IsAllowedAnthropic(options.BaseUrl));
        }

        OpenRouterOptions openRouter = _openRouterOptions.CurrentValue;
        return new(
            LanguageModelProviders.OpenRouter,
            openRouter.BaseUrl,
            openRouter.Model,
            openRouter.Timeout,
            openRouter.MaximumRetries,
            openRouter.MaximumResponseBytes,
            SecretKeys.OpenRouterApiKey,
            ProviderProtocol.OpenRouter,
            ProviderEndpointPolicy.IsAllowedOpenRouter(openRouter.BaseUrl),
            openRouter.RefererUrl,
            openRouter.ApplicationTitle);
    }

    private static string? ReadOpenAIContent(JsonElement root) =>
        root.TryGetProperty("choices", out JsonElement choices)
        && choices.ValueKind == JsonValueKind.Array
        && choices.GetArrayLength() > 0
        && choices[0].TryGetProperty("message", out JsonElement message)
        && message.TryGetProperty("content", out JsonElement content)
            ? content.GetString()
            : null;

    private static string? ReadAnthropicContent(JsonElement root)
    {
        if (!root.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out JsonElement type)
                && type.GetString() == "text"
                && block.TryGetProperty("text", out JsonElement text))
            {
                return text.GetString();
            }
        }

        return null;
    }

    private static int? ReadAnthropicTokens(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage))
        {
            return null;
        }

        int input = usage.TryGetProperty("input_tokens", out JsonElement inputElement)
            && inputElement.TryGetInt32(out int inputTokens)
                ? inputTokens
                : 0;
        int output = usage.TryGetProperty("output_tokens", out JsonElement outputElement)
            && outputElement.TryGetInt32(out int outputTokens)
                ? outputTokens
                : 0;
        return input + output is int total && total > 0 ? total : null;
    }

    private static string? TryReadOpenAIDelta(string data)
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
            return null;
        }
    }

    private static string? TryReadAnthropicDelta(string data)
    {
        try
        {
            using JsonDocument chunk = JsonDocument.Parse(data);
            return chunk.RootElement.TryGetProperty("type", out JsonElement type)
                && type.GetString() == "content_block_delta"
                && chunk.RootElement.TryGetProperty("delta", out JsonElement delta)
                && delta.TryGetProperty("text", out JsonElement text)
                    ? text.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (ModelErrorCategory Category, bool ShouldRetry, string Description)
        ClassifyFailure(HttpStatusCode statusCode, string providerName) => statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (
                ModelErrorCategory.Authentication,
                false,
                $"{providerName} rejected the API key. Check it in Settings."),
            HttpStatusCode.TooManyRequests => (
                ModelErrorCategory.RateLimited,
                true,
                $"{providerName} is rate-limiting requests. StormATC will use deterministic phraseology."),
            HttpStatusCode.NotFound or HttpStatusCode.BadRequest => (
                ModelErrorCategory.InvalidModel,
                false,
                $"{providerName} rejected the model or structured-output request. Check the model ID and its structured-output support."),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => (
                ModelErrorCategory.Timeout,
                true,
                $"{providerName} timed out."),
            >= HttpStatusCode.InternalServerError => (
                ModelErrorCategory.Transient,
                true,
                $"{providerName} reported a server error."),
            _ => (
                ModelErrorCategory.Unknown,
                false,
                $"{providerName} returned HTTP {(int)statusCode}."),
        };

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        double baseDelayMs = 500 * Math.Pow(2, attempt);
        double jitterMs = _jitter.Next(0, 250);
        await Task.Delay(TimeSpan.FromMilliseconds(baseDelayMs + jitterMs), cancellationToken)
            .ConfigureAwait(false);
    }

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

    private static string Truncate(string value) =>
        value.Length <= 2000 ? value : value[..2000] + "… (truncated)";

    private enum ProviderProtocol
    {
        OpenRouter,
        OpenAI,
        Groq,
        DeepSeek,
        Anthropic,
    }

    private sealed record ProviderConfiguration(
        string Name,
        string BaseUrl,
        string Model,
        TimeSpan Timeout,
        int MaximumRetries,
        int MaximumResponseBytes,
        string SecretKey,
        ProviderProtocol Protocol,
        bool EndpointAllowed,
        string? RefererUrl = null,
        string? ApplicationTitle = null);
}
