using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Application.Phraseology;
using StormATC.Application.Transmissions;
using StormATC.Domain.Atc;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.OpenRouter;

/// <summary>
/// Asks a language model to say an already-approved decision in natural controller speech.
/// </summary>
/// <remarks>
/// <para>
/// The model is a <em>speaker</em>, never a decider. It receives the facts the engine approved
/// and the deterministic wording as a reference, and its only job is to express the same thing
/// more naturally. It is not told what it might have granted, and it has no channel through which
/// to change session state.
/// </para>
/// <para>
/// Its output is checked by <see cref="PhraseologyValidator"/> before it can be spoken, so a
/// fluent-but-wrong altitude is caught and the deterministic wording is used instead. This class
/// returns null on any failure, which the coordinator reads as "keep the deterministic output".
/// </para>
/// </remarks>
public sealed class OpenRouterPhraseologyRenderer(
    ILanguageModelClient client,
    IPromptCatalog promptCatalog,
    IOptionsMonitor<LanguageModelOptions> options,
    IOptionsMonitor<AtcRealismOptions> realismOptions,
    ILogger<OpenRouterPhraseologyRenderer> logger) : ILanguageModelPhraseologyRenderer
{
    private static readonly JsonSchemaDefinition Schema = new()
    {
        Name = "controller_transmission",
        Strict = true,
        Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["spokenText"],
          "properties": {
            "spokenText": { "type": "string" }
          }
        }
        """,
    };

    /// <summary>
    /// Longest response accepted, in characters.
    /// </summary>
    /// <remarks>
    /// A controller transmission is one or two sentences. Anything longer is the model
    /// explaining itself, adding traffic information, or drifting — none of which belongs on the
    /// frequency.
    /// </remarks>
    private const int MaximumSpokenLength = 400;

    private readonly ILanguageModelClient _client =
        client ?? throw new ArgumentNullException(nameof(client));

    private readonly IPromptCatalog _promptCatalog =
        promptCatalog ?? throw new ArgumentNullException(nameof(promptCatalog));

    private readonly IOptionsMonitor<LanguageModelOptions> _options = options
        ?? throw new ArgumentNullException(nameof(options));

    private readonly IOptionsMonitor<AtcRealismOptions> _realismOptions = realismOptions
        ?? throw new ArgumentNullException(nameof(realismOptions));

    private readonly ILogger<OpenRouterPhraseologyRenderer> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public bool IsAvailable =>
        _client.IsConfigured && _realismOptions.CurrentValue.UseLanguageModelPhraseology;

    /// <inheritdoc />
    public async Task<RenderedPhraseology?> RenderAsync(
        AtcDecision decision,
        AtcSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(session);

        if (!IsAvailable || !decision.AllowLanguageModelPhraseology)
        {
            return null;
        }

        // The controller's own prompt sits on top of the global rules, so a position's
        // behaviour can be edited without touching the shared ones.
        string globalRules = await _promptCatalog
            .GetPromptAsync(PromptNames.GlobalControllerRules, cancellationToken)
            .ConfigureAwait(false);

        string rendererPrompt = await _promptCatalog
            .GetPromptAsync(PromptNames.PhraseologyRenderer, cancellationToken)
            .ConfigureAwait(false);

        string controllerPrompt = await _promptCatalog
            .GetPromptAsync(PromptNames.ForController(session.CurrentController), cancellationToken)
            .ConfigureAwait(false);

        if (rendererPrompt.Length == 0)
        {
            _logger.LogWarning(
                "The phraseology renderer prompt is missing, so deterministic wording was used.");

            return null;
        }

        var systemPrompt = new StringBuilder(globalRules.Length + rendererPrompt.Length + controllerPrompt.Length + 16);
        Append(systemPrompt, globalRules);
        Append(systemPrompt, controllerPrompt);
        Append(systemPrompt, rendererPrompt);

        StructuredModelResponse<TransmissionResponse> response = await _client
            .GenerateStructuredAsync<TransmissionResponse>(
                new ModelRequest
                {
                    SystemPrompt = systemPrompt.ToString(),
                    UserPrompt = BuildUserPrompt(decision, session),
                    Temperature = _options.CurrentValue.PhraseologyTemperature,

                    // Some OpenRouter models spend part of this budget on structured-output
                    // scaffolding or reasoning. Two hundred tokens truncated an otherwise valid
                    // JSON string in a real clearance. The spoken-text length guard below still
                    // rejects any rambling answer.
                    MaxOutputTokens = 500,
                },
                Schema,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.Success || response.Value?.SpokenText is not { Length: > 0 } spoken)
        {
            _logger.LogInformation(
                "The phraseology model returned nothing usable ({Category}); using deterministic wording.",
                response.ErrorCategory);

            return null;
        }

        spoken = spoken.Trim();

        if (spoken.Length > MaximumSpokenLength)
        {
            _logger.LogWarning(
                "The phraseology model returned {Length} characters, which is too long for a transmission.",
                spoken.Length);

            return null;
        }

        return new RenderedPhraseology
        {
            SpokenText = spoken,
            DisplayText = session.Callsign is Callsign callsign
                ? DeterministicPhraseology.ToDisplayForm(spoken, callsign)
                : spoken,
            UsedDeterministicFallback = false,
            Duration = response.Duration,
        };

        static void Append(StringBuilder builder, string section)
        {
            if (section.Length == 0)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append(section);
        }
    }

    /// <summary>
    /// Builds the user message: the approved facts, the prohibitions, and the reference wording.
    /// </summary>
    /// <remarks>
    /// The deterministic phraseology is included on purpose. Giving the model a correct example
    /// to rephrase produces far better output than asking it to compose from facts alone, and it
    /// makes the failure mode benign: the worst plausible answer is the reference wording back
    /// again.
    /// </remarks>
    private static string BuildUserPrompt(AtcDecision decision, AtcSession session)
    {
        var builder = new StringBuilder(512);

        builder.Append("Controller position: ")
            .AppendLine(DeterministicPhraseology.SpokenControllerName(session.CurrentController));

        if (session.Callsign is Callsign callsign)
        {
            builder.Append("Aircraft callsign, spoken: ").AppendLine(callsign.ToSpoken());
        }

        builder.Append("Response type: ").AppendLine(decision.ResponseType.ToString());
        builder.AppendLine();

        builder.AppendLine("Approved facts. These are the only values you may state:");
        foreach (KeyValuePair<string, string> fact in decision.ApprovedFacts)
        {
            builder.Append("  - ")
                .Append(fact.Key)
                .Append(": ")
                .AppendLine(fact.Value);
        }

        if (decision.ProhibitedFacts.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("You must not state or imply any of the following:");
            foreach (string prohibited in decision.ProhibitedFacts)
            {
                builder.Append("  - ").AppendLine(prohibited);
            }
        }

        builder.AppendLine();
        builder.AppendLine("Reference wording, which is already correct:");
        builder.Append("  ").AppendLine(decision.FallbackPhraseology);

        builder.AppendLine();
        builder.AppendLine(
            "Return the reference wording unchanged unless a punctuation-only cadence improvement "
            + "controller does — digits individually, 'decimal' for the point. Add nothing: no "
            + "traffic, weather, greeting, coaching, explanation, or value not listed above.");

        return builder.ToString();
    }

    /// <summary>The wire shape. Untrusted until the validator has passed it.</summary>
    internal sealed record TransmissionResponse
    {
        [JsonPropertyName("spokenText")]
        public string SpokenText { get; init; } = string.Empty;
    }
}
