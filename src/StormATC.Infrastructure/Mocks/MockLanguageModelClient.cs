using System.Runtime.CompilerServices;
using StormATC.Application.Abstractions;

namespace StormATC.Infrastructure.Mocks;

/// <summary>
/// A language model client that is deliberately never available.
/// </summary>
/// <remarks>
/// <para>
/// This is not a stub that returns canned intelligent-looking answers. Faking model output
/// would hide exactly the thing the prototype needs to prove: that StormATC is fully functional
/// with no language model at all. Every call reports <see cref="ModelErrorCategory.NotConfigured"/>,
/// which drives the pipeline down its deterministic path — the same path a real OpenRouter
/// outage produces.
/// </para>
/// <para>
/// The result is that running in offline mode is a genuine test of the fallback behaviour, not a
/// separate code path that only exists in demos.
/// </para>
/// </remarks>
public sealed class MockLanguageModelClient : ILanguageModelClient
{
    /// <inheritdoc />
    public string ProviderName => "Mock (no language model)";

    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public Task<StructuredModelResponse<T>> GenerateStructuredAsync<T>(
        ModelRequest request,
        JsonSchemaDefinition schema,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(StructuredModelResponse.Fail<T>(
            "No language model is configured. StormATC is using deterministic parsing and phraseology.",
            ModelErrorCategory.NotConfigured,
            TimeSpan.Zero));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelTextDelta> StreamTextAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Yields nothing: there is no model to stream from.
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc />
    public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(ProviderTestResult.Fail(
            "Offline mode is on, so no language model is available. Turn it off in Settings and "
            + "add an OpenRouter API key and model to enable it."));
    }
}
