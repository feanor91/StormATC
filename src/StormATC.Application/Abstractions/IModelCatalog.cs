namespace StormATC.Application.Abstractions;

/// <summary>One model a provider offers, as shown in the Settings model pickers.</summary>
/// <param name="Id">The identifier sent to the provider, such as <c>openai/gpt-4o-mini</c>.</param>
/// <param name="Name">A human-readable name; falls back to the identifier.</param>
/// <param name="IsFree">True when the provider lists the model at no cost.</param>
public sealed record ModelChoice(string Id, string Name, bool IsFree);

/// <summary>The outcome of asking a provider which models it offers.</summary>
public sealed record ModelCatalogResult
{
    /// <summary>True when the provider answered with a model list.</summary>
    public required bool Success { get; init; }

    /// <summary>The models, free ones first. Empty on failure.</summary>
    public IReadOnlyList<ModelChoice> Models { get; init; } = [];

    /// <summary>A short explanation when the list could not be fetched.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Creates a failed result.</summary>
    public static ModelCatalogResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>Lists the models a language-model provider offers.</summary>
public interface IModelCatalog
{
    /// <summary>
    /// Fetches the models of <paramref name="provider"/> (a <c>LanguageModelProviders</c> name).
    /// Never throws for network or authorisation problems; they come back as a failed result.
    /// </summary>
    Task<ModelCatalogResult> ListModelsAsync(string provider, CancellationToken cancellationToken);
}
