using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Domain;
using StormATC.Domain.Airports;
using StormATC.Domain.Sessions;

namespace StormATC.Infrastructure.Simulator;

/// <summary>
/// Produces optional background controller-and-pilot exchanges for observed simulator traffic.
/// </summary>
/// <remarks>
/// This service never creates, moves, or controls traffic. FSLTL or another injector supplies the
/// aircraft and MSFS flies them. All generation and speech switches default off, every exchange
/// yields to user push-to-talk, and an hourly cap bounds third-party API use.
/// </remarks>
public sealed class TrafficRadioService(
    MsfsSimConnectConnector connector,
    AtcSessionContext sessionContext,
    ILanguageModelClient languageModel,
    ITextToSpeechService textToSpeech,
    IPromptCatalog prompts,
    RadioActivityState radioActivity,
    IOptionsMonitor<TrafficOptions> trafficOptions,
    IOptionsMonitor<ApplicationOptions> applicationOptions,
    IClock clock,
    ILogger<TrafficRadioService> logger) : BackgroundService
{
    private static readonly JsonSchemaDefinition ChatterSchema = new()
    {
        Name = "traffic_radio_exchange",
        Strict = true,
        Schema = """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["controllerLine", "pilotLine"],
              "properties": {
                "controllerLine": { "type": "string", "minLength": 1, "maxLength": 160 },
                "pilotLine": { "type": "string", "minLength": 1, "maxLength": 160 }
              }
            }
            """,
    };

    private static readonly string[] ForbiddenGeneratedPhrases =
    [
        "cleared for takeoff",
        "cleared to land",
        "cleared approach",
        "cross runway",
        "pushback approved",
        "squawk",
        "flight level",
        "runway ",
        "heading ",
        "frequency ",
        "mayday",
        "pan pan",
    ];

    private readonly Queue<DateTimeOffset> _exchangeTimes = new();
    private DateTimeOffset _nextAllowedAt = DateTimeOffset.MinValue;
    private string? _lastCallsign;
    private bool _wasEnabled;

    /// <summary>Raised after a text exchange is generated, whether or not speech is enabled.</summary>
    public event EventHandler<TrafficRadioExchangeEventArgs>? ExchangeGenerated;

    /// <summary>Current integration state for Settings and Diagnostics.</summary>
    public string Status { get; private set; } = UiText.T("Traffic radio is disabled.");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await GenerateIfDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Status = UiText.T("Traffic radio encountered an error; see Diagnostics.");
                logger.LogWarning(ex, "Background traffic radio generation failed.");
            }
        }
    }

    private async Task GenerateIfDueAsync(CancellationToken cancellationToken)
    {
        TrafficOptions options = trafficOptions.CurrentValue;
        bool enabled = options.EnableLiveTraffic && options.EnableRadioChatter;
        if (!enabled)
        {
            _wasEnabled = false;
            Status = options.EnableLiveTraffic
                ? UiText.T("Live traffic is enabled; radio chatter is off.")
                : UiText.T("Traffic integration is disabled.");
            return;
        }

        DateTimeOffset now = clock.UtcNow;
        if (!_wasEnabled)
        {
            _wasEnabled = true;
            _nextAllowedAt = now.AddSeconds(15);
            Status = UiText.T("Traffic radio is armed and waiting for nearby aircraft.");
            return;
        }

        if (now < _nextAllowedAt
            || radioActivity.UserIsTransmitting
            || textToSpeech.IsSpeaking)
        {
            return;
        }

        if (options.PauseChatterWhenOnlineAtcClientDetected && IsOnlineAtcClientRunning())
        {
            Status = UiText.T("Traffic chatter is paused while an online ATC client is running.");
            _nextAllowedAt = now.AddMinutes(1);
            return;
        }

        PruneHourlyBudget(now);
        if (_exchangeTimes.Count >= options.MaximumExchangesPerHour)
        {
            Status = UiText.T($"Hourly traffic-radio limit reached ({options.MaximumExchangesPerHour}).");
            _nextAllowedAt = _exchangeTimes.Peek().AddHours(1);
            return;
        }

        MsfsTrafficSnapshot? traffic = SelectTraffic();
        if (traffic is null)
        {
            Status = UiText.T("Traffic radio is armed; no suitable nearby aircraft are visible.");
            _nextAllowedAt = now.AddSeconds(30);
            return;
        }

        string callsign = NormalizeCallsign(traffic);
        TrafficRadioExchange exchange = BuildDeterministicExchange(traffic, callsign);

        if (options.UseLanguageModelForChatter
            && !applicationOptions.CurrentValue.OfflineMode
            && languageModel.IsConfigured)
        {
            exchange = await TryGenerateModelExchangeAsync(
                traffic,
                callsign,
                exchange,
                cancellationToken).ConfigureAwait(false);
        }

        _exchangeTimes.Enqueue(now);
        _lastCallsign = callsign;
        _nextAllowedAt = now.Add(options.MinimumInterval);
        Status = traffic.IsFslTlModel
            ? UiText.T($"Last exchange used FSLTL traffic {callsign}.")
            : UiText.T($"Last exchange used simulator traffic {callsign}.");

        ExchangeGenerated?.Invoke(
            this,
            new TrafficRadioExchangeEventArgs(exchange, traffic.IsFslTlModel, now));

        if (options.SpeakChatter)
        {
            await textToSpeech
                .SpeakAsync(
                    exchange.ControllerLine,
                    VoiceConfiguration.Default with { VoiceKey = $"chatter-atc:{Guid.NewGuid():N}" },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!radioActivity.UserIsTransmitting)
            {
                await textToSpeech
                    .SpeakAsync(
                        exchange.PilotLine,
                        VoiceConfiguration.Default with { VoiceKey = $"chatter-pilot:{Guid.NewGuid():N}" },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private MsfsTrafficSnapshot? SelectTraffic() =>
        connector.NearbyTraffic
            .Where(contact => contact.GroundSpeedKnots >= 2 || !contact.IsOnGround)
            .OrderByDescending(contact => contact.IsFslTlModel)
            .ThenBy(contact => string.Equals(
                NormalizeCallsign(contact),
                _lastCallsign,
                StringComparison.OrdinalIgnoreCase))
            .ThenBy(contact => contact.Position.DistanceNauticalMilesTo(
                sessionContext.CurrentSession.AircraftState.Position))
            .FirstOrDefault();

    private async Task<TrafficRadioExchange> TryGenerateModelExchangeAsync(
        MsfsTrafficSnapshot traffic,
        string callsign,
        TrafficRadioExchange fallback,
        CancellationToken cancellationToken)
    {
        string systemPrompt = await prompts
            .GetPromptAsync(PromptNames.TrafficChatter, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            return fallback;
        }

        AtcSession session = sessionContext.CurrentSession;
        string situation = traffic.IsOnGround
            ? "moving on the airport surface"
            : traffic.VerticalSpeedFeetPerMinute switch
            {
                > 400 => "airborne and climbing",
                < -400 => "airborne and descending",
                _ => "airborne and level",
            };

        var request = new ModelRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = JsonSerializer.Serialize(new
            {
                callsign,
                situation,
                controller = session.CurrentController.ToString(),
                source = traffic.IsFslTlModel ? "FSLTL model" : "MSFS traffic",
            }),
            Temperature = 0.35,
            MaxOutputTokens = 160,
            CorrelationId = $"traffic-{traffic.ObjectId}-{Guid.CreateVersion7():N}",
        };

        StructuredModelResponse<TrafficChatterResponse> result = await languageModel
            .GenerateStructuredAsync<TrafficChatterResponse>(
                request,
                ChatterSchema,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success
            || result.Value is not TrafficChatterResponse generated
            || !IsSafeGeneratedExchange(generated, callsign))
        {
            logger.LogDebug(
                "Traffic chatter model output was unavailable or rejected; using local wording.");
            return fallback;
        }

        return new TrafficRadioExchange(
            generated.ControllerLine.Trim(),
            generated.PilotLine.Trim(),
            callsign,
            UsedLanguageModel: true);
    }

    private static bool IsSafeGeneratedExchange(
        TrafficChatterResponse response,
        string callsign)
    {
        if (string.IsNullOrWhiteSpace(response.ControllerLine)
            || string.IsNullOrWhiteSpace(response.PilotLine)
            || response.ControllerLine.Length > 160
            || response.PilotLine.Length > 160
            || !response.ControllerLine.Contains(callsign, StringComparison.OrdinalIgnoreCase)
            || !response.PilotLine.Contains(callsign, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string combined = $"{response.ControllerLine} {response.PilotLine}";
        return !ForbiddenGeneratedPhrases.Any(phrase =>
            combined.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static TrafficRadioExchange BuildDeterministicExchange(
        MsfsTrafficSnapshot traffic,
        string callsign) =>
        traffic.IsOnGround
            ? new TrafficRadioExchange(
                $"{callsign}, taxi with caution and continue as cleared.",
                $"Taxi with caution, continuing as cleared, {callsign}.",
                callsign,
                UsedLanguageModel: false)
            : new TrafficRadioExchange(
                $"{callsign}, radar contact, continue as cleared.",
                $"Continuing as cleared, {callsign}.",
                callsign,
                UsedLanguageModel: false);

    private static string NormalizeCallsign(MsfsTrafficSnapshot traffic)
    {
        string candidate = traffic.Callsign.Trim();
        return candidate.Length is > 0 and <= 24
            && !candidate.Contains("FSLTL", StringComparison.OrdinalIgnoreCase)
            ? candidate
            : $"Traffic {traffic.ObjectId}";
    }

    private void PruneHourlyBudget(DateTimeOffset now)
    {
        while (_exchangeTimes.TryPeek(out DateTimeOffset timestamp)
            && now - timestamp >= TimeSpan.FromHours(1))
        {
            _exchangeTimes.Dequeue();
        }
    }

    private static bool IsOnlineAtcClientRunning()
    {
        foreach (string processName in new[] { "vPilot", "xPilot", "swift", "PilotEdge" })
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                bool found = processes.Length > 0;
                foreach (Process process in processes)
                {
                    process.Dispose();
                }

                if (found)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // A process may exit while being enumerated. Treat that as not detected.
            }
        }

        return false;
    }

    private sealed record TrafficChatterResponse
    {
        public string ControllerLine { get; init; } = string.Empty;
        public string PilotLine { get; init; } = string.Empty;
    }
}

/// <summary>One generated background radio exchange.</summary>
public sealed record TrafficRadioExchange(
    string ControllerLine,
    string PilotLine,
    string Callsign,
    bool UsedLanguageModel);

/// <summary>Carries a generated exchange to the UI.</summary>
public sealed class TrafficRadioExchangeEventArgs(
    TrafficRadioExchange exchange,
    bool isFslTlTraffic,
    DateTimeOffset timestamp) : EventArgs
{
    public TrafficRadioExchange Exchange { get; } = exchange;
    public bool IsFslTlTraffic { get; } = isFslTlTraffic;
    public DateTimeOffset Timestamp { get; } = timestamp;
}
