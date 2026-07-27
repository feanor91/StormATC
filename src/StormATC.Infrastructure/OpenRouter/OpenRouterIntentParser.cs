using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Options;
using StormATC.Domain;
using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.OpenRouter;

/// <summary>
/// Asks a language model to interpret a transmission the deterministic parser could not.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place a model is allowed to influence what StormATC believes the pilot
/// said — and even here its answer is a <em>hypothesis</em>, not a fact. Everything it returns
/// is re-validated against the domain before it is used: an intent name that is not in the
/// enumeration, a runway that does not parse, a squawk with an eight in it, a confidence outside
/// zero to one — any of those and the whole answer is discarded and the deterministic result
/// stands.
/// </para>
/// <para>
/// The model never sees, and could not affect, whether a clearance is granted. That decision
/// belongs to the deterministic engine, which reads the validated intent and nothing else.
/// </para>
/// </remarks>
public sealed class OpenRouterIntentParser(
    ILanguageModelClient client,
    IPromptCatalog promptCatalog,
    IOptionsMonitor<OpenRouterOptions> options,
    ILogger<OpenRouterIntentParser> logger) : ILanguageModelIntentParser
{
    /// <summary>
    /// The schema the provider must constrain output to.
    /// </summary>
    /// <remarks>
    /// Deliberately flat and small. A model asked for a deep object produces more malformed
    /// answers, and every field here is one the domain can validate independently.
    /// </remarks>
    private static readonly string[] AllowedIntentNames = Enum.GetNames<PilotIntentType>();

    private static readonly JsonSchemaDefinition Schema = new()
    {
        Name = "pilot_intent",
        Strict = true,
        Schema = $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["intent", "confidence"],
          "properties": {
            "intent": {
              "type": "string",
              "enum": {{JsonSerializer.Serialize(AllowedIntentNames)}}
            },
            "confidence": { "type": "number" },
            "callsign": { "type": ["string", "null"] },
            "runway": { "type": ["string", "null"] },
            "altitudeFeet": { "type": ["integer", "null"] },
            "flightLevel": { "type": ["integer", "null"] },
            "squawk": { "type": ["string", "null"] },
            "frequencyMhz": { "type": ["number", "null"] },
            "headingDegrees": { "type": ["integer", "null"] },
            "taxiways": { "type": ["array", "null"], "items": { "type": "string" } },
            "holdShortRunway": { "type": ["string", "null"] },
            "waypoint": { "type": ["string", "null"] },
            "approachName": { "type": ["string", "null"] },
            "stand": { "type": ["string", "null"] },
            "needsClarification": { "type": ["boolean", "null"] },
            "clarificationReason": { "type": ["string", "null"] }
          }
        }
        """,
    };

    private readonly ILanguageModelClient _client =
        client ?? throw new ArgumentNullException(nameof(client));

    private readonly IPromptCatalog _promptCatalog =
        promptCatalog ?? throw new ArgumentNullException(nameof(promptCatalog));

    private readonly IOptionsMonitor<OpenRouterOptions> _options = options
        ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<OpenRouterIntentParser> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public bool IsAvailable => _client.IsConfigured;

    /// <inheritdoc />
    public async Task<PilotIntent?> ParseAsync(
        string normalizedTranscript,
        AtcSession session,
        PilotIntent deterministicHypothesis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedTranscript);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(deterministicHypothesis);

        if (!IsAvailable)
        {
            return null;
        }

        string systemPrompt = await _promptCatalog
            .GetPromptAsync(PromptNames.PilotIntentParser, cancellationToken)
            .ConfigureAwait(false);

        if (systemPrompt.Length == 0)
        {
            // Without the prompt the model has no rules to follow. Silently sending an
            // unguided request would produce exactly the unconstrained output this design
            // exists to avoid.
            _logger.LogWarning(
                "The pilot intent parser prompt is missing, so the language model was not consulted.");

            return null;
        }

        StructuredModelResponse<IntentResponse> response = await _client
            .GenerateStructuredAsync<IntentResponse>(
                new ModelRequest
                {
                    SystemPrompt = systemPrompt,
                    UserPrompt = BuildUserPrompt(normalizedTranscript, session, deterministicHypothesis),
                    Temperature = _options.CurrentValue.IntentParserTemperature,
                    MaxOutputTokens = 400,
                },
                Schema,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.Success || response.Value is not IntentResponse value)
        {
            _logger.LogInformation(
                "The intent model returned no usable answer ({Category}); keeping the deterministic result.",
                response.ErrorCategory);

            return null;
        }

        return Validate(value, normalizedTranscript, session);
    }

    /// <summary>
    /// Builds the user message.
    /// </summary>
    /// <remarks>
    /// Carries only what is needed to disambiguate: the transcript, the aircraft's own callsign,
    /// the controller currently worked, the flight phase, and what readback is outstanding. No
    /// clearance values are supplied, because the model's job is to say what the pilot meant,
    /// not to reconstruct what the controller said.
    /// </remarks>
    private static string BuildUserPrompt(
        string transcript,
        AtcSession session,
        PilotIntent deterministic)
    {
        var builder = new StringBuilder(512);

        builder.Append("Transmission: \"").Append(transcript).AppendLine("\"");
        builder.Append("Aircraft callsign: ").AppendLine(session.Callsign?.Normalized ?? "unknown");
        builder.Append("Controller worked: ").AppendLine(session.CurrentController.ToString());
        builder.Append("Flight phase: ").AppendLine(session.Phase.ToString());

        if (session.PendingReadback is PendingReadback pending)
        {
            builder.Append("Readback outstanding for a ")
                .Append(pending.ClearanceType)
                .Append(" clearance, covering: ")
                .AppendLine(string.Join(
                    ", ",
                    pending.ExpectedItems
                        .Where(item => !pending.SatisfiedItems.Contains(item.Type))
                        .Select(item => item.Type.ToString())));
        }
        else
        {
            builder.AppendLine("No readback is outstanding.");
        }

        builder.Append("The local parser guessed ")
            .Append(deterministic.Type)
            .Append(" with confidence ")
            .Append(deterministic.Confidence.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine(".");

        builder.Append("Allowed intent names: ")
            .AppendLine(string.Join(", ", AllowedIntentNames));

        builder.AppendLine(
            "Classify the transmission. Use the exact intent names from the list in your instructions. "
            + "Report only values the pilot actually said; use null for anything absent.");

        return builder.ToString();
    }

    /// <summary>
    /// Re-derives every value through the domain types, discarding the answer if anything fails.
    /// </summary>
    /// <remarks>
    /// All-or-nothing rather than field-by-field salvage. A model that got the runway wrong may
    /// well have got the intent wrong too, and a half-trusted answer is harder to reason about
    /// than a rejected one.
    /// </remarks>
    private PilotIntent? Validate(IntentResponse response, string transcript, AtcSession session)
    {
        if (!Enum.TryParse(response.Intent, ignoreCase: true, out PilotIntentType type)
            || !Enum.IsDefined(type))
        {
            _logger.LogWarning(
                "The intent model returned an unknown intent name; the answer was discarded.");

            return null;
        }

        if (response.Confidence is < 0 or > 1)
        {
            _logger.LogWarning(
                "The intent model returned a confidence outside 0 to 1; the answer was discarded.");

            return null;
        }

        Callsign? callsign = null;
        if (response.Callsign is { Length: > 0 } spokenCallsign
            && !Callsign.TryCreate(spokenCallsign, out callsign))
        {
            _logger.LogWarning("The intent model returned an unusable callsign; the answer was discarded.");
            return null;
        }

        RunwayIdentifier? runway = null;
        if (response.Runway is { Length: > 0 } runwayText)
        {
            if (!RunwayIdentifier.TryCreate(runwayText, out RunwayIdentifier parsedRunway))
            {
                _logger.LogWarning("The intent model returned an invalid runway; the answer was discarded.");
                return null;
            }

            runway = parsedRunway;
        }

        RunwayIdentifier? holdShort = null;
        if (response.HoldShortRunway is { Length: > 0 } holdShortText)
        {
            if (!RunwayIdentifier.TryCreate(holdShortText, out RunwayIdentifier parsedHoldShort))
            {
                _logger.LogWarning("The intent model returned an invalid hold-short runway; discarded.");
                return null;
            }

            holdShort = parsedHoldShort;
        }

        Altitude? altitude = null;
        if (response.FlightLevel is int level)
        {
            if (level is < 0 or > (Altitude.MaximumFeet / 100))
            {
                _logger.LogWarning("The intent model returned an out-of-range flight level; discarded.");
                return null;
            }

            altitude = Altitude.FromFlightLevel(level);
        }
        else if (response.AltitudeFeet is int feet)
        {
            if (!Altitude.TryFromFeet(feet, out Altitude parsedAltitude))
            {
                _logger.LogWarning("The intent model returned an out-of-range altitude; discarded.");
                return null;
            }

            altitude = parsedAltitude;
        }

        SquawkCode? squawk = null;
        if (response.Squawk is { Length: > 0 } squawkText)
        {
            if (!SquawkCode.TryCreate(squawkText, out SquawkCode parsedSquawk))
            {
                _logger.LogWarning("The intent model returned an invalid squawk; the answer was discarded.");
                return null;
            }

            squawk = parsedSquawk;
        }

        Frequency? frequency = null;
        if (response.FrequencyMhz is double megahertz)
        {
            if (!Frequency.TryFromMegahertz((decimal)megahertz, out Frequency parsedFrequency))
            {
                _logger.LogWarning("The intent model returned a frequency outside the band; discarded.");
                return null;
            }

            frequency = parsedFrequency;
        }

        Heading? heading = null;
        if (response.HeadingDegrees is int degrees)
        {
            if (!Heading.TryValidate(degrees, out Heading parsedHeading))
            {
                _logger.LogWarning("The intent model returned an invalid heading; the answer was discarded.");
                return null;
            }

            heading = parsedHeading;
        }

        // Taxiway designators are single letters at every aerodrome StormATC models.
        var taxiways = new List<string>();
        foreach (string taxiway in response.Taxiways ?? [])
        {
            string trimmed = taxiway.Trim().ToUpperInvariant();
            if (trimmed.Length is not 1 || !char.IsAsciiLetterUpper(trimmed[0]))
            {
                _logger.LogWarning("The intent model returned an unusable taxiway; the answer was discarded.");
                return null;
            }

            taxiways.Add(trimmed);
        }

        return new PilotIntent
        {
            Type = type,
            Confidence = response.Confidence,
            Callsign = callsign ?? session.Callsign,
            Entities = new IntentEntities
            {
                Runway = runway,
                Altitude = altitude,
                Squawk = squawk,
                Frequency = frequency,
                Heading = heading,
                Taxiways = taxiways,
                HoldShortRunway = holdShort,
                MentionsHoldShort = holdShort is not null,
                Waypoint = Trim(response.Waypoint),
                ApproachName = Trim(response.ApproachName),
                Stand = Trim(response.Stand),
            },
            Source = IntentParserSource.LanguageModel,
            SourceTranscript = transcript,
            RequiresClarification = response.NeedsClarification ?? false,
            ClarificationReason = Trim(response.ClarificationReason),
        };

        static string? Trim(string? value) =>
            value is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    /// <summary>The wire shape. Every field is untrusted until <see cref="Validate"/> has run.</summary>
    internal sealed record IntentResponse
    {
        [JsonPropertyName("intent")]
        public string Intent { get; init; } = string.Empty;

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }

        [JsonPropertyName("callsign")]
        public string? Callsign { get; init; }

        [JsonPropertyName("runway")]
        public string? Runway { get; init; }

        [JsonPropertyName("altitudeFeet")]
        public int? AltitudeFeet { get; init; }

        [JsonPropertyName("flightLevel")]
        public int? FlightLevel { get; init; }

        [JsonPropertyName("squawk")]
        public string? Squawk { get; init; }

        [JsonPropertyName("frequencyMhz")]
        public double? FrequencyMhz { get; init; }

        [JsonPropertyName("headingDegrees")]
        public int? HeadingDegrees { get; init; }

        [JsonPropertyName("taxiways")]
        public IReadOnlyList<string>? Taxiways { get; init; }

        [JsonPropertyName("holdShortRunway")]
        public string? HoldShortRunway { get; init; }

        [JsonPropertyName("waypoint")]
        public string? Waypoint { get; init; }

        [JsonPropertyName("approachName")]
        public string? ApproachName { get; init; }

        [JsonPropertyName("stand")]
        public string? Stand { get; init; }

        [JsonPropertyName("needsClarification")]
        public bool? NeedsClarification { get; init; }

        [JsonPropertyName("clarificationReason")]
        public string? ClarificationReason { get; init; }
    }
}
