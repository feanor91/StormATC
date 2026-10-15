using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Infrastructure.Security;

namespace StormATC.Infrastructure.OpenRouter;

/// <summary>
/// Lists the models each language-model provider offers, so Settings can show a picker instead
/// of a blank text box.
/// </summary>
/// <remarks>
/// OpenRouter's catalogue is public and is fetched without a key. The other providers only list
/// their models to an authenticated caller, so their key is read from the secret store, checked
/// against <see cref="ProviderEndpointPolicy"/> first, and sent nowhere else. Redirects are
/// disabled by registration, as for the completion client.
/// </remarks>
public sealed partial class ModelCatalogClient(
    HttpClient httpClient,
    IOptionsMonitor<OpenRouterOptions> openRouterOptions,
    IOptionsMonitor<OpenAIOptions> openAIOptions,
    IOptionsMonitor<GroqOptions> groqOptions,
    IOptionsMonitor<DeepSeekOptions> deepSeekOptions,
    IOptionsMonitor<AnthropicOptions> anthropicOptions,
    IOptionsMonitor<ApplicationOptions> applicationOptions,
    ISecretStore secretStore,
    ILogger<ModelCatalogClient> logger) : IModelCatalog
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public async Task<ModelCatalogResult> ListModelsAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        if (applicationOptions.CurrentValue.OfflineMode)
        {
            return ModelCatalogResult.Fail(UiText.T("Offline mode is on: model lists are not fetched."));
        }

        if (!applicationOptions.CurrentValue.CloudServicesConsentGiven)
        {
            return ModelCatalogResult.Fail(
                UiText.T("Cloud services are disabled until consent is enabled in Settings."));
        }

        string name = LanguageModelProviders.Normalize(provider);
        (string baseUrl, bool allowed, string secretKey, string path) = Describe(name);
        if (!allowed)
        {
            return ModelCatalogResult.Fail(
                UiText.T($"The configured {name} endpoint was blocked for security."));
        }

        string? apiKey = null;
        if (name != LanguageModelProviders.OpenRouter)
        {
            apiKey = await secretStore.GetSecretAsync(secretKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return ModelCatalogResult.Fail(UiText.T($"Save a {name} API key to list its models."));
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"{baseUrl.TrimEnd('/')}/{path}"));
            if (apiKey is not null)
            {
                if (name == LanguageModelProviders.Anthropic)
                {
                    message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                    message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }
                else
                {
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }
            }

            using HttpResponseMessage response = await httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Provider} model list returned HTTP {Status}.", name, (int)response.StatusCode);
                return ModelCatalogResult.Fail(UiText.T($"{name} refused the request (HTTP {(int)response.StatusCode})."));
            }

            await using Stream body = await response.Content
                .ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(body, cancellationToken: timeout.Token)
                .ConfigureAwait(false);

            IReadOnlyList<ModelChoice> models = ParseModels(name, document.RootElement);
            return models.Count == 0
                ? ModelCatalogResult.Fail(UiText.T($"{name} returned no models."))
                : new ModelCatalogResult { Success = true, Models = models };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ModelCatalogResult.Fail(UiText.T($"{name} did not respond in time."));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "Listing {Provider} models failed.", name);
            return ModelCatalogResult.Fail(UiText.T($"{name} could not be reached."));
        }
    }

    /// <summary>
    /// Turns a provider's <c>models</c> response into picker entries, free models first and
    /// each group in alphabetical order. Models that cannot produce text are dropped.
    /// </summary>
    public static IReadOnlyList<ModelChoice> ParseModels(string provider, JsonElement root)
    {
        var models = new List<ModelChoice>();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        bool isOpenRouter = provider == LanguageModelProviders.OpenRouter;
        foreach (JsonElement item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out JsonElement idElement)
                || idElement.GetString() is not { Length: > 0 } id)
            {
                continue;
            }

            if (!isOpenRouter && NonChatModel().IsMatch(id))
            {
                continue;
            }

            if (isOpenRouter && !ProducesText(item))
            {
                continue;
            }

            string name = item.TryGetProperty("name", out JsonElement nameElement)
                && nameElement.GetString() is { Length: > 0 } displayName
                    ? displayName
                    : id;
            bool free = isOpenRouter && IsFreeOnOpenRouter(id, item);
            models.Add(new ModelChoice(id, name, free));
        }

        return [.. models
            .OrderByDescending(model => model.IsFree)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)];
    }

    // OpenRouter also lists image, music and video models; only text output suits ATC replies.
    private static bool ProducesText(JsonElement item) =>
        !item.TryGetProperty("architecture", out JsonElement architecture)
        || architecture.ValueKind != JsonValueKind.Object
        || !architecture.TryGetProperty("output_modalities", out JsonElement outputs)
        || outputs.ValueKind != JsonValueKind.Array
        || outputs.EnumerateArray().Any(output => output.GetString() == "text");

    private static bool IsFreeOnOpenRouter(string id, JsonElement item)
    {
        if (id.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return item.TryGetProperty("pricing", out JsonElement pricing)
            && pricing.ValueKind == JsonValueKind.Object
            && IsZero(pricing, "prompt")
            && IsZero(pricing, "completion");
    }

    private static bool IsZero(JsonElement pricing, string property) =>
        pricing.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal price)
        && price == 0m;

    private (string BaseUrl, bool Allowed, string SecretKey, string Path) Describe(string provider)
    {
        switch (provider)
        {
            case LanguageModelProviders.OpenAI:
                string openAI = openAIOptions.CurrentValue.BaseUrl;
                return (openAI, ProviderEndpointPolicy.IsAllowedOpenAI(openAI), SecretKeys.OpenAIApiKey, "models");
            case LanguageModelProviders.Groq:
                string groq = groqOptions.CurrentValue.BaseUrl;
                return (groq, ProviderEndpointPolicy.IsAllowedGroq(groq), SecretKeys.GroqApiKey, "models");
            case LanguageModelProviders.DeepSeek:
                string deepSeek = deepSeekOptions.CurrentValue.BaseUrl;
                return (deepSeek, ProviderEndpointPolicy.IsAllowedDeepSeek(deepSeek), SecretKeys.DeepSeekApiKey, "models");
            case LanguageModelProviders.Anthropic:
                string anthropic = anthropicOptions.CurrentValue.BaseUrl;
                return (anthropic, ProviderEndpointPolicy.IsAllowedAnthropic(anthropic), SecretKeys.AnthropicApiKey, "v1/models?limit=100");
            default:
                string openRouter = openRouterOptions.CurrentValue.BaseUrl;
                return (openRouter, ProviderEndpointPolicy.IsAllowedOpenRouter(openRouter), SecretKeys.OpenRouterApiKey, "models");
        }
    }

    [GeneratedRegex("embed|whisper|tts|dall-e|moderation|davinci|babbage|transcribe|realtime|image|audio|guard|safeguard", RegexOptions.IgnoreCase)]
    private static partial Regex NonChatModel();
}
