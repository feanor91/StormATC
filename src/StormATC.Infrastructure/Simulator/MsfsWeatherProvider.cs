using StormATC.Application.Abstractions;
using StormATC.Domain;
using StormATC.Domain.Atc;

namespace StormATC.Infrastructure.Simulator;

/// <summary>
/// Supplies live ambient MSFS conditions, with fictional sample weather as an offline fallback.
/// </summary>
public sealed class MsfsWeatherProvider(
    MsfsSimConnectConnector connector,
    Mocks.MockWeatherProvider fallback) : IWeatherProvider
{
    private readonly MsfsSimConnectConnector _connector =
        connector ?? throw new ArgumentNullException(nameof(connector));
    private readonly Mocks.MockWeatherProvider _fallback =
        fallback ?? throw new ArgumentNullException(nameof(fallback));

    /// <inheritdoc />
    public Task<WeatherSnapshot?> GetAirportWeatherAsync(
        string icao,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_connector.ConnectionState == SimulatorConnectionState.Connected
            && _connector.LatestWeather is WeatherSnapshot live)
        {
            return Task.FromResult<WeatherSnapshot?>(live);
        }

        return _fallback.GetAirportWeatherAsync(icao, cancellationToken);
    }
}
