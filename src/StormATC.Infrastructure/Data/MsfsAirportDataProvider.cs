using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Domain.Airports;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Infrastructure.Data;

/// <summary>
/// Supplies bundled airport files when present and otherwise requests the airport directly
/// from the connected Microsoft Flight Simulator facility database.
/// </summary>
public sealed class MsfsAirportDataProvider(
    JsonAirportDataProvider bundledProvider,
    MsfsSimConnectConnector simulator,
    ILogger<MsfsAirportDataProvider> logger) : IAirportDataProvider
{
    private readonly JsonAirportDataProvider _bundledProvider =
        bundledProvider ?? throw new ArgumentNullException(nameof(bundledProvider));

    private readonly MsfsSimConnectConnector _simulator =
        simulator ?? throw new ArgumentNullException(nameof(simulator));

    private readonly ILogger<MsfsAirportDataProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<AirportData?> GetAirportAsync(
        string icao,
        CancellationToken cancellationToken)
    {
        AirportData? bundled = await _bundledProvider
            .GetAirportAsync(icao, cancellationToken)
            .ConfigureAwait(false);

        // Hand-authored data remains authoritative for the fictional demonstration airports.
        if (bundled?.SupportsTaxiRouting == true)
        {
            return bundled;
        }

        AirportData? live = await _simulator
            .GetAirportDataAsync(icao, cancellationToken)
            .ConfigureAwait(false);
        if (live is not null)
        {
            return live;
        }

        if (bundled is null)
        {
            _logger.LogInformation(
                "No bundled or live MSFS airport data is currently available for {Icao}.",
                icao);
        }

        return bundled;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAvailableAirportsAsync(
        CancellationToken cancellationToken) =>
        _bundledProvider.ListAvailableAirportsAsync(cancellationToken);
}
