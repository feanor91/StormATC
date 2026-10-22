using System.Text.Json;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.Localization;

namespace StormATC.Application.Charts;

/// <summary>
/// Reads a chart in the charts folder with the pilot's own language model and proposes a VFR profile.
/// </summary>
/// <remarks>
/// <para>
/// The pages are drawn as images and sent to the provider the pilot chose in Settings (which needs the
/// cloud-services consent and a model that reads images). The model answers with a strict JSON document
/// in which every fact quotes the words of the chart it was read from.
/// </para>
/// <para>
/// The model never writes a profile: <see cref="VacProfileDraftBuilder"/> checks its answer, the pilot
/// confirms each fact, and only then is the profile saved. The ATC engine reads a saved profile like any
/// other, and the model has no say in what the engine permits.
/// </para>
/// </remarks>
public sealed class VacProfileImporter(
    IVacChartLibrary charts,
    ILanguageModelClient languageModel,
    ILogger<VacProfileImporter> logger)
{
    /// <summary>How many pages of a chart are sent: the facts are on a chart's first pages.</summary>
    public const int MaximumPages = 12;

    /// <summary>The width the pages are drawn at: wide enough to read small print.</summary>
    public const int PageWidth = 1500;

    private const string SystemPrompt = """
        You read aeronautical charts for a flight simulator's ATC companion. The images are the pages, in order, of one
        aerodrome's chart set (AIP "AD 2" visual approach / visual landing charts and the text pages that follow).
        Extract only what the pages state. Never guess and never use outside knowledge: when a page does not give a fact,
        leave it null. Quote, in the language of the chart, the words or the label the fact is read from ("evidence") and
        give the page number it is on. Say "high" in "confidence" only when the chart states the fact plainly in words or in a
        table; say "low" when you inferred it from a drawing or the print is hard to read.

        Facts to extract:
        - icao and name of the aerodrome.
        - preferredQfu: the runway direction the chart says is preferred ("QFU préférentiel"), in degrees magnetic.
        - circuitHeightFeetAmsl: the traffic circuit height in feet above mean sea level, if the chart gives it (convert from
          a height above ground only when the chart gives the aerodrome elevation).
        - circuitSide: on which side of the runways the circuit lies (north, south, east or west), if the chart says it in words
          or draws it clearly.
        - reportingPoints: every VFR reporting point of the aerodrome, with its designator exactly as on the chart (one to six
          letters or digits, such as E, S, NH, SM), how a French controller says it (spokenFrench, for example "Écho",
          "Sierra Mike", "Novembre Hôtel") and an English controller (spokenEnglish: "Echo", "Sierra Mike", "November Hotel"),
          and its coordinates in decimal degrees when the chart gives them. Read tables row by row: do not shift a row's
          coordinates onto another row's name.

        Reply with the JSON object of the schema and nothing else.
        """;

    /// <summary>Reads a chart and proposes a profile for the pilot to check.</summary>
    /// <param name="chartId">The chart, as listed in the charts folder.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<VacImportResult> AnalyzeAsync(string chartId, CancellationToken cancellationToken)
    {
        VacChart? chart = charts.List().FirstOrDefault(c => c.Id.Equals(chartId, StringComparison.OrdinalIgnoreCase));
        if (chart is null)
        {
            return Failed(UiText.T("This chart is not in the charts folder any more."));
        }

        if (!languageModel.IsConfigured)
        {
            return Failed(UiText.T("Reading a chart needs a language model that reads images: enable cloud services and choose a provider, a model and a key in Settings."));
        }

        int pages = await charts.GetPageCountAsync(chart.Id, cancellationToken).ConfigureAwait(false) ?? 0;
        if (pages == 0)
        {
            return Failed(UiText.T("This chart could not be opened."));
        }

        List<ModelImage> images = [];
        for (int page = 1; page <= Math.Min(pages, MaximumPages); page++)
        {
            VacChartPage? drawn = await charts.RenderPageAsync(chart.Id, page, PageWidth, cancellationToken).ConfigureAwait(false);
            if (drawn is not null)
            {
                images.Add(new ModelImage(drawn.Png));
            }
        }

        if (images.Count == 0)
        {
            return Failed(UiText.T("This chart could not be drawn."));
        }

        var request = new ModelRequest
        {
            SystemPrompt = SystemPrompt,
            UserPrompt = $"Chart file: {chart.Name}. The {images.Count} images are its pages 1 to {images.Count}, in order. Extract the facts.",
            Temperature = 0,
            MaxOutputTokens = 4000,
            Images = images,
            TimeoutOverride = TimeSpan.FromMinutes(3),
            CorrelationId = Guid.NewGuid().ToString("N"),
        };

        StructuredModelResponse<VacExtraction> response = await languageModel
            .GenerateStructuredAsync<VacExtraction>(request, Schema(), cancellationToken)
            .ConfigureAwait(false);

        if (!response.Success || response.Value is null)
        {
            logger.LogInformation(
                "Reading the chart {Chart} failed ({Category}): {Message}",
                chart.Name,
                response.ErrorCategory,
                response.ErrorMessage);
            return new VacImportResult
            {
                Success = false,
                PagesSent = images.Count,
                Provider = languageModel.ProviderName,
                Message = (response.ErrorMessage ?? UiText.T("The model did not answer."))
                    + " " + UiText.T("Check that the model you chose reads images."),
            };
        }

        VacImportResult checkedResult = VacProfileDraftBuilder.Build(response.Value, chart);
        logger.LogInformation(
            "Read the chart {Chart} with {Provider}: {Pages} pages, {Facts} facts.",
            chart.Name,
            languageModel.ProviderName,
            images.Count,
            checkedResult.Draft?.Facts.Count ?? 0);
        return checkedResult with
        {
            PagesSent = images.Count,
            Provider = languageModel.ProviderName,
            Tokens = response.TotalTokens,
        };
    }

    private static VacImportResult Failed(string message) => new() { Success = false, Message = message };

    /// <summary>The JSON schema the model must answer with (every field present, null when the chart does not say).</summary>
    public static JsonSchemaDefinition Schema() => new()
    {
        Name = "vac_profile_extraction",
        Strict = true,
        Schema = """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["icao", "name", "preferredQfu", "circuitHeightFeetAmsl", "circuitSide", "reportingPoints"],
              "properties": {
                "icao": { "type": "string" },
                "name": { "type": "string" },
                "preferredQfu": { "anyOf": [ { "type": "null" }, { "$ref": "#/$defs/number" } ] },
                "circuitHeightFeetAmsl": { "anyOf": [ { "type": "null" }, { "$ref": "#/$defs/number" } ] },
                "circuitSide": {
                  "anyOf": [
                    { "type": "null" },
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["side", "evidence", "page", "confidence"],
                      "properties": {
                        "side": { "type": ["string", "null"], "enum": ["north", "south", "east", "west", null] },
                        "evidence": { "type": "string" },
                        "page": { "type": ["integer", "null"] },
                        "confidence": { "type": "string", "enum": ["high", "low"] }
                      }
                    }
                  ]
                },
                "reportingPoints": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["name", "spokenFrench", "spokenEnglish", "latitude", "longitude", "evidence", "page", "confidence"],
                    "properties": {
                      "name": { "type": "string" },
                      "spokenFrench": { "type": "string" },
                      "spokenEnglish": { "type": "string" },
                      "latitude": { "type": ["number", "null"] },
                      "longitude": { "type": ["number", "null"] },
                      "evidence": { "type": "string" },
                      "page": { "type": ["integer", "null"] },
                      "confidence": { "type": "string", "enum": ["high", "low"] }
                    }
                  }
                }
              },
              "$defs": {
                "number": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["value", "evidence", "page", "confidence"],
                  "properties": {
                    "value": { "type": ["integer", "null"] },
                    "evidence": { "type": "string" },
                    "page": { "type": ["integer", "null"] },
                    "confidence": { "type": "string", "enum": ["high", "low"] }
                  }
                }
              }
            }
            """,
    };
}
