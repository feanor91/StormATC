using System.Text.Json;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Infrastructure.OpenRouter;

namespace StormATC.Infrastructure.Tests;

/// <summary>How a provider's model list becomes the Settings model pickers.</summary>
public class ModelCatalogClientTests
{
    [Fact]
    public void OpenRouter_lists_free_models_first_then_alphabetically()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {"data":[
              {"id":"zeta/paid","name":"Zeta","pricing":{"prompt":"0.000001","completion":"0.000002"}},
              {"id":"beta/model:free","name":"Beta (free)","pricing":{"prompt":"0","completion":"0"}},
              {"id":"alpha/paid","name":"Alpha","pricing":{"prompt":"0.000003","completion":"0.000004"}},
              {"id":"gamma/zero","name":"Gamma","pricing":{"prompt":"0","completion":"0"}},
              {"id":"half/free","name":"Half","pricing":{"prompt":"0","completion":"0.000001"}}
            ]}
            """);

        IReadOnlyList<ModelChoice> models = ModelCatalogClient.ParseModels(
            LanguageModelProviders.OpenRouter,
            document.RootElement);

        Assert.Equal(
            ["beta/model:free", "gamma/zero", "alpha/paid", "half/free", "zeta/paid"],
            models.Select(model => model.Id).ToArray());
        Assert.Equal([true, true, false, false, false], models.Select(model => model.IsFree).ToArray());
    }

    [Fact]
    public void Other_providers_drop_models_that_cannot_chat_and_never_mark_free()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {"data":[
              {"id":"gpt-4o-mini"},
              {"id":"text-embedding-3-small"},
              {"id":"whisper-1"},
              {"id":"gpt-4o"}
            ]}
            """);

        IReadOnlyList<ModelChoice> models = ModelCatalogClient.ParseModels(
            LanguageModelProviders.OpenAI,
            document.RootElement);

        Assert.Equal(["gpt-4o", "gpt-4o-mini"], models.Select(model => model.Id).ToArray());
        Assert.All(models, model => Assert.False(model.IsFree));
    }

    [Fact]
    public void OpenRouter_drops_models_that_do_not_output_text()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {"data":[
              {"id":"a/chat","architecture":{"output_modalities":["text"]}},
              {"id":"b/music","architecture":{"output_modalities":["audio"]},"pricing":{"prompt":"0","completion":"0"}}
            ]}
            """);

        Assert.Equal(
            ["a/chat"],
            ModelCatalogClient.ParseModels(LanguageModelProviders.OpenRouter, document.RootElement)
                .Select(model => model.Id)
                .ToArray());
    }

    [Fact]
    public void A_response_without_a_data_array_gives_an_empty_list()
    {
        using JsonDocument document = JsonDocument.Parse("""{"error":"nope"}""");

        Assert.Empty(ModelCatalogClient.ParseModels(
            LanguageModelProviders.Groq,
            document.RootElement));
    }
}
