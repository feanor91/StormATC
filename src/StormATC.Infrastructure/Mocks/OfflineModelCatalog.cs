using StormATC.Application.Abstractions;
using StormATC.Application.Localization;

namespace StormATC.Infrastructure.Mocks;

/// <summary>A model catalogue for offline mode: it never contacts a provider.</summary>
public sealed class OfflineModelCatalog : IModelCatalog
{
    /// <inheritdoc />
    public Task<ModelCatalogResult> ListModelsAsync(string provider, CancellationToken cancellationToken) =>
        Task.FromResult(ModelCatalogResult.Fail(UiText.T("Offline mode is on: model lists are not fetched.")));
}
