using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Infrastructure.OpenRouter;

namespace StormATC.Infrastructure.Tests;

/// <summary>Provider routing, authentication, and structured-output wire formats.</summary>
public sealed class LanguageModelProviderClientTests
{
    [Theory]
    [InlineData("OpenRouter", "openrouter.ai", "/api/v1/chat/completions", "json_schema")]
    [InlineData("OpenAI", "api.openai.com", "/v1/chat/completions", "max_completion_tokens")]
    [InlineData("Groq", "api.groq.com", "/openai/v1/chat/completions", "json_schema")]
    [InlineData("DeepSeek", "api.deepseek.com", "/chat/completions", "json_object")]
    [InlineData("Anthropic", "api.anthropic.com", "/v1/messages", "output_config")]
    public async Task Selected_provider_receives_its_own_wire_format(
        string provider,
        string expectedHost,
        string expectedPath,
        string expectedBodyFragment)
    {
        var handler = new RecordingHandler(provider == LanguageModelProviders.Anthropic);
        OpenRouterClient client = CreateClient(provider, handler);

        StructuredModelResponse<SampleResponse> result =
            await client.GenerateStructuredAsync<SampleResponse>(
                new ModelRequest
                {
                    SystemPrompt = "Return the approved phrase.",
                    UserPrompt = "approval: pushback approved",
                    MaxOutputTokens = 50,
                },
                new JsonSchemaDefinition
                {
                    Name = "sample",
                    Schema =
                        """{"type":"object","additionalProperties":false,"required":["spokenText"],"properties":{"spokenText":{"type":"string"}}}""",
                },
                CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("ok", result.Value?.SpokenText);
        Assert.Equal(expectedHost, handler.RequestUri?.Host);
        Assert.Equal(expectedPath, handler.RequestUri?.AbsolutePath);
        Assert.Contains(expectedBodyFragment, handler.Body, StringComparison.Ordinal);

        if (provider == LanguageModelProviders.Anthropic)
        {
            Assert.Equal("secret", handler.ApiKeyHeader);
            Assert.Equal("2023-06-01", handler.AnthropicVersion);
            Assert.Null(handler.AuthorizationScheme);
        }
        else
        {
            Assert.Equal("Bearer", handler.AuthorizationScheme);
            Assert.Equal("secret", handler.AuthorizationParameter);
        }
    }

    private static OpenRouterClient CreateClient(string provider, HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new StubOptionsMonitor<LanguageModelOptions>(
                new LanguageModelOptions { Provider = provider }),
            new StubOptionsMonitor<OpenRouterOptions>(
                new OpenRouterOptions { Model = "test/model" }),
            new StubOptionsMonitor<OpenAIOptions>(
                new OpenAIOptions { Model = "gpt-test" }),
            new StubOptionsMonitor<GroqOptions>(
                new GroqOptions { Model = "groq-test" }),
            new StubOptionsMonitor<DeepSeekOptions>(
                new DeepSeekOptions { Model = "deepseek-test" }),
            new StubOptionsMonitor<AnthropicOptions>(
                new AnthropicOptions { Model = "claude-test" }),
            new StubOptionsMonitor<ApplicationOptions>(
                new ApplicationOptions { CloudServicesConsentGiven = true }),
            new AnyKeySecretStore(),
            NullLogger<OpenRouterClient>.Instance);

    private sealed class RecordingHandler(bool anthropic) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? ApiKeyHeader { get; private set; }
        public string? AnthropicVersion { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            ApiKeyHeader = ReadHeader(request, "x-api-key");
            AnthropicVersion = ReadHeader(request, "anthropic-version");

            string json = anthropic
                ? """{"content":[{"type":"text","text":"{\"spokenText\":\"ok\"}"}],"usage":{"input_tokens":1,"output_tokens":2}}"""
                : """{"choices":[{"message":{"content":"{\"spokenText\":\"ok\"}"}}],"usage":{"total_tokens":3}}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        private static string? ReadHeader(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values)
                ? values.Single()
                : null;
    }

    private sealed class StubOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class AnyKeySecretStore : ISecretStore
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("secret");

        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RemoveSecretAsync(string key, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> HasSecretAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed record SampleResponse
    {
        public string SpokenText { get; init; } = string.Empty;
    }
}
