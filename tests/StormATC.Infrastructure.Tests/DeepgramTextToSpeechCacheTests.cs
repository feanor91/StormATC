using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Infrastructure.Speech;

namespace StormATC.Infrastructure.Tests;

public sealed class DeepgramTextToSpeechCacheTests
{
    [Fact]
    public async Task Named_cache_synthesizes_once_until_the_broadcast_text_changes()
    {
        var handler = new CountingAudioHandler();
        await using var service = new DeepgramTextToSpeechService(
            new HttpClient(handler),
            new StubOptionsMonitor<DeepgramOptions>(new DeepgramOptions()),
            new StubOptionsMonitor<AudioOptions>(new AudioOptions
            {
                CleanAudioMode = true,
                TransmissionClicksEnabled = false,
            }),
            new StubOptionsMonitor<ApplicationOptions>(new ApplicationOptions
            {
                CloudServicesConsentGiven = true,
            }),
            new AnyKeySecretStore(),
            NullLogger<DeepgramTextToSpeechService>.Instance);

        Task<bool> firstCall = service.SpeakCachedAsync(
            "departure-atis",
            "Information Alfa.",
            VoiceConfiguration.Default,
            CancellationToken.None);
        Task<bool> repeatedCall = service.SpeakCachedAsync(
            "departure-atis",
            "Information Alfa.",
            VoiceConfiguration.Default,
            CancellationToken.None);
        bool[] initialResults = await Task.WhenAll(firstCall, repeatedCall);
        bool updated = await service.SpeakCachedAsync(
            "departure-atis",
            "Information Bravo.",
            VoiceConfiguration.Default,
            CancellationToken.None);

        Assert.All(initialResults, Assert.True);
        Assert.True(updated);
        Assert.Equal(2, handler.RequestCount);
    }

    private sealed class CountingAudioHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            await Task.Delay(20, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                // One tenth of a second of silent 24 kHz, 16-bit mono PCM.
                Content = new ByteArrayContent(new byte[4_800]),
            };
        }
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
            Task.FromResult<string?>("test-secret");

        public Task SetSecretAsync(
            string key,
            string value,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RemoveSecretAsync(string key, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> HasSecretAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
