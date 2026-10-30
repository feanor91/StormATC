using StormATC.Application.Abstractions;

namespace StormATC.Infrastructure.Weather;

/// <summary>
/// Reads the latest METAR of an aerodrome from the Aviation Weather Center (aviationweather.gov), the US
/// National Weather Service's public service. It needs no account or key.
/// </summary>
/// <remarks>
/// Only the aerodrome's ICAO code leaves the machine, and only when the pilot asks for a report on the Radio
/// page.
/// </remarks>
public sealed class AviationWeatherMetarProvider(HttpClient httpClient) : IMetarProvider
{
    private const string SourceName = "aviationweather.gov";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    /// <inheritdoc />
    public async Task<MetarReport?> GetLatestAsync(string icao, CancellationToken cancellationToken)
    {
        if (!StormATC.Application.Weather.AirportBriefingService.IsValidIcao(icao))
        {
            return null;
        }

        var uri = new Uri(
            $"https://aviationweather.gov/api/data/metar?ids={Uri.EscapeDataString(icao.ToUpperInvariant())}&format=raw",
            UriKind.Absolute);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using HttpResponseMessage response = await _httpClient
            .GetAsync(uri, timeout.Token)
            .ConfigureAwait(false);

        // 204 is how the service says it holds no report for that aerodrome.
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        string? firstLine = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return firstLine is null ? null : new MetarReport(firstLine, SourceName);
    }
}
