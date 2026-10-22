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

    private static readonly JsonSchemaDefinition SampleSchema = new()
    {
        Name = "sample",
        Schema = """{"type":"object","additionalProperties":false,"required":["spokenText"],"properties":{"spokenText":{"type":"string"}}}""",
    };

    private static ModelRequest ChartRequest() => new()
    {
        SystemPrompt = "Read the chart.",
        UserPrompt = "Extract the facts.",
        Images = [new ModelImage([1, 2, 3]), new ModelImage([4, 5, 6])],
        TimeoutOverride = TimeSpan.FromMinutes(3),
    };

    [Theory]
    [InlineData("OpenRouter")]
    [InlineData("OpenAI")]
    [InlineData("Groq")]
    public async Task Images_go_to_openai_style_providers_as_data_urls_in_the_user_message(string provider)
    {
        var handler = new RecordingHandler(anthropic: false);
        OpenRouterClient client = CreateClient(provider, handler);

        StructuredModelResponse<SampleResponse> result =
            await client.GenerateStructuredAsync<SampleResponse>(ChartRequest(), SampleSchema, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(handler.Body);
        System.Text.Json.JsonElement user = body.RootElement.GetProperty("messages")[1];
        Assert.Equal("user", user.GetProperty("role").GetString());
        System.Text.Json.JsonElement parts = user.GetProperty("content");
        Assert.Equal(3, parts.GetArrayLength());
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal(
            "data:image/png;base64," + Convert.ToBase64String([1, 2, 3]),
            parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Images_go_to_anthropic_as_base64_blocks_before_the_text()
    {
        var handler = new RecordingHandler(anthropic: true);
        OpenRouterClient client = CreateClient("Anthropic", handler);

        StructuredModelResponse<SampleResponse> result =
            await client.GenerateStructuredAsync<SampleResponse>(ChartRequest(), SampleSchema, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(handler.Body);
        System.Text.Json.JsonElement content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("image/png", content[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), content[0].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("text", content[2].GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_provider_that_does_not_read_images_refuses_them_without_sending_anything()
    {
        var handler = new RecordingHandler(anthropic: false);
        OpenRouterClient client = CreateClient("DeepSeek", handler);

        StructuredModelResponse<SampleResponse> result =
            await client.GenerateStructuredAsync<SampleResponse>(ChartRequest(), SampleSchema, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ModelErrorCategory.NotConfigured, result.ErrorCategory);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task A_request_without_images_keeps_plain_text_messages()
    {
        var handler = new RecordingHandler(anthropic: false);
        OpenRouterClient client = CreateClient("OpenAI", handler);

        await client.GenerateStructuredAsync<SampleResponse>(
            new ModelRequest { SystemPrompt = "s", UserPrompt = "u" }, SampleSchema, CancellationToken.None);

        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(handler.Body);
        Assert.Equal("u", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
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
