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
using StormATC.Domain.ValueObjects;

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

        // Nobody hears the radio while the simulator is in its menus (it then sends no data at all), nor with the
        // radio switched off.
        if (sessionContext.CurrentSession.AircraftState.InSimulatorMenus || IsStale(sessionContext.CurrentSession.AircraftState))
        {
            Status = UiText.T("Traffic radio is silent while the simulator is in its menus.");
            return;
        }

        if (!sessionContext.CurrentSession.AircraftState.Com1Powered)
        {
            Status = UiText.T("Traffic radio is silent: the aircraft's radio has no power.");
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

        // A pilot hears only the aircraft the controller of the tuned frequency works: the traffic of the
        // flight information sector, or the airborne traffic near an Approach, Departure or Centre frequency
        // of his aerodromes, or the surface traffic of their Ground and Tower. Anything else is silence.
        Func<MsfsTrafficSnapshot, bool> heardOnFrequency = await BuildFrequencyFilterAsync(cancellationToken).ConfigureAwait(false);
        MsfsTrafficSnapshot? traffic = SelectTraffic(heardOnFrequency);
        if (traffic is null)
        {
            Status = UiText.T("Traffic radio is armed; no aircraft is working the frequency you are tuned to.");
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

        if (options.SpeakChatter && CanBeHeard())
        {
            await textToSpeech
                .SpeakAsync(
                    exchange.ControllerLine,
                    VoiceConfiguration.Default with { VoiceKey = $"chatter-atc:{Guid.NewGuid():N}" },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!radioActivity.UserIsTransmitting && CanBeHeard())
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

    /// <summary>True when the simulator has sent no data for a while: it is in a menu, closed, or paused.</summary>
    private bool IsStale(Domain.Aircraft.AircraftState state) =>
        clock.UtcNow - state.Timestamp > TimeSpan.FromSeconds(15);

    /// <summary>True while the pilot is in the aircraft with the radio powered: what he would still hear.</summary>
    private bool CanBeHeard()
    {
        Domain.Aircraft.AircraftState state = sessionContext.CurrentSession.AircraftState;
        return !state.InSimulatorMenus && state.Com1Powered && !IsStale(state);
    }

    /// <summary>How far from an aerodrome the airborne traffic of its Approach, Departure or Centre is, in nautical miles.</summary>
    private const double ControlledAirspaceMiles = 40;

    /// <summary>How far from an aerodrome the surface traffic of its Ground and Tower is, in nautical miles.</summary>
    private const double SurfaceMiles = 5;

    /// <summary>
    /// Builds the test of whether an aircraft is on the frequency COM1 is tuned to. Only the aerodromes of the flight plan
    /// and the flight information sectors are known, so any other frequency hears nobody.
    /// </summary>
    private async Task<Func<MsfsTrafficSnapshot, bool>> BuildFrequencyFilterAsync(CancellationToken cancellationToken)
    {
        AtcSession session = sessionContext.CurrentSession;
        var airports = new List<AirportData>();

        if (session.FlightPlan is { } plan)
        {
            foreach (string icao in new[] { plan.DepartureIcao, plan.DestinationIcao }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (await sessionContext.GetAirportAsync(icao, cancellationToken).ConfigureAwait(false) is { } airport)
                {
                    airports.Add(airport);
                }
            }
        }

        return FrequencyFilter(session.AircraftState.Radio.ActiveTransmitFrequency, airports);
    }

    /// <summary>The test of whether an aircraft is on the frequency, given the aerodromes the pilot knows.</summary>
    /// <param name="tuned">The frequency COM1 is tuned to.</param>
    /// <param name="airports">The departure and destination aerodromes.</param>
    internal static Func<MsfsTrafficSnapshot, bool> FrequencyFilter(Frequency tuned, IReadOnlyList<AirportData> airports)
    {
        FlightInformationSector? sector = FlightInformationRegions.FindByFrequency(tuned);
        var zones = new List<(GeoPosition Center, double Miles, bool Surface)>();

        foreach (AirportData airport in airports)
        {
            AirportFrequencies published = airport.Frequencies;
            if (Matches(tuned, published.Approach, published.Departure, published.Center))
            {
                zones.Add((airport.ReferencePosition, ControlledAirspaceMiles, false));
            }

            if (Matches(tuned, published.Ground, published.Tower, published.ClearanceDelivery))
            {
                zones.Add((airport.ReferencePosition, SurfaceMiles, true));
            }
        }

        return contact =>
            (sector is not null && !contact.IsOnGround && FlightInformationRegions.Find(contact.Position) == sector)
            || zones.Any(zone => contact.IsOnGround == zone.Surface && contact.Position.IsWithin(zone.Center, zone.Miles));
    }

    private static bool Matches(Frequency tuned, params Frequency?[] candidates) =>
        candidates.Any(candidate => candidate is Frequency published && tuned.IsSameChannel(published));

    private MsfsTrafficSnapshot? SelectTraffic(Func<MsfsTrafficSnapshot, bool> heardOnFrequency) =>
        connector.NearbyTraffic
            .Where(contact => contact.GroundSpeedKnots >= 2 || !contact.IsOnGround)
            .Where(heardOnFrequency)
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
