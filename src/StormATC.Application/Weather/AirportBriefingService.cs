using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.Atis;
using StormATC.Application.Localization;
using StormATC.Application.Sessions;
using StormATC.Domain.Airports;
using StormATC.Domain.Phraseology;
using StormATC.Domain.ValueObjects;
using StormATC.Domain.Weather;

namespace StormATC.Application.Weather;

/// <summary>What the Radio page shows for an aerodrome the pilot typed in.</summary>
public sealed record AirportBriefing
{
    /// <summary>The aerodrome.</summary>
    public required string Icao { get; init; }

    /// <summary>The report as published, or null when there is none.</summary>
    public string? RawMetar { get; init; }

    /// <summary>Where the report came from.</summary>
    public string? Source { get; init; }

    /// <summary>The report line by line, in plain words.</summary>
    public IReadOnlyList<MetarLine> Lines { get; init; } = [];

    /// <summary>The ATIS made from the report, or null when there is no report to make it from.</summary>
    public AtisBroadcast? Atis { get; init; }

    /// <summary>What happened, in a sentence the pilot can act on.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Gives the real METAR of an aerodrome, decoded, and the ATIS that goes with it. It works from the simulator's
/// menus because it needs neither the aircraft nor a flight plan.
/// </summary>
/// <remarks>
/// The weather is the real one, as published for pilots. It is not read from the simulator, so it can differ
/// from what the simulator plays once the flight has begun, for example when the pilot chose custom weather.
/// </remarks>
public sealed class AirportBriefingService(
    IMetarProvider metarProvider,
    AtcSessionContext sessionContext,
    IClock clock,
    ILogger<AirportBriefingService> logger)
{
    private readonly IMetarProvider _metarProvider =
        metarProvider ?? throw new ArgumentNullException(nameof(metarProvider));

    private readonly AtcSessionContext _sessionContext =
        sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly ILogger<AirportBriefingService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>True when the text is the four letters or digits of an ICAO code.</summary>
    /// <param name="icao">What the pilot typed.</param>
    public static bool IsValidIcao(string? icao) =>
        icao is { Length: 4 } && icao.All(char.IsAsciiLetterOrDigit);

    /// <summary>Fetches the report for an aerodrome and builds what goes with it.</summary>
    /// <param name="icao">The aerodrome's ICAO code.</param>
    /// <param name="frequency">The frequency the pilot typed, or null.</param>
    /// <param name="language">The language of the descriptions and of the ATIS.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<AirportBriefing> GetAsync(
        string icao,
        Frequency? frequency,
        PhraseologyLanguage language,
        CancellationToken cancellationToken)
    {
        string code = (icao ?? string.Empty).Trim().ToUpperInvariant();
        if (!IsValidIcao(code))
        {
            return new AirportBriefing
            {
                Icao = code,
                Message = UiText.T("Enter the four-letter ICAO code of the aerodrome, for example LFPO."),
            };
        }

        MetarReport? report;
        try
        {
            report = await _metarProvider.GetLatestAsync(code, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The METAR of {Icao} could not be fetched.", code);
            return new AirportBriefing
            {
                Icao = code,
                Message = UiText.T("The weather service could not be reached. Check your internet connection."),
            };
        }

        if (report is null || !MetarParser.TryParse(report.Raw, out DecodedMetar metar))
        {
            return new AirportBriefing
            {
                Icao = code,
                RawMetar = report?.Raw,
                Source = report?.Source,
                Message = report is null
                    ? UiText.T($"No METAR is published for {code}.")
                    : UiText.T($"The report for {code} could not be read."),
            };
        }

        AirportData? airport = await _sessionContext
            .GetAirportAsync(code, cancellationToken)
            .ConfigureAwait(false);

        AtisBroadcast atis = AtisBroadcastBuilder.BuildForAerodrome(
            code,
            frequency,
            language,
            airport,
            metar.ToWeatherSnapshot(report.Source),
            _clock.UtcNow);

        return new AirportBriefing
        {
            Icao = code,
            RawMetar = metar.Raw,
            Source = report.Source,
            Lines = MetarDescriber.Describe(metar, language),
            Atis = atis,
            Message = UiText.T($"METAR of {code} from {report.Source}."),
        };
    }
}
