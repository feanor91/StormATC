using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;

namespace StormATC.Infrastructure.Panel;

/// <summary>Reads NavVFR's local feed (loopback only) for the panel's map tab.</summary>
public sealed class NavVfrFeedClient : INavVfrFeed
{
    /// <summary>The largest answer accepted: a crowded map is a few hundred kilobytes.</summary>
    private const int MaximumBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger<NavVfrFeedClient> _logger;

    /// <summary>Creates the client.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="options">The panel options, which hold NavVFR's port.</param>
    /// <param name="logger">The logger.</param>
    public NavVfrFeedClient(HttpClient http, IOptions<ToolbarPanelOptions> options, ILogger<NavVfrFeedClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http.BaseAddress ??= new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{options.Value.NavVfrPort}/"));
        _http.Timeout = TimeSpan.FromSeconds(3);
    }

    /// <inheritdoc />
    public Task<byte[]?> GetStateAsync(CancellationToken cancellationToken) =>
        GetAsync("api/state", cancellationToken);

    /// <inheritdoc />
    public Task<byte[]?> GetMapAsync(
        double minLatitude,
        double minLongitude,
        double maxLatitude,
        double maxLongitude,
        CancellationToken cancellationToken)
    {
        static string Number(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
        return GetAsync(
            $"api/map?minLat={Number(minLatitude)}&minLon={Number(minLongitude)}&maxLat={Number(maxLatitude)}&maxLon={Number(maxLongitude)}",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<NavVfrTile?> GetTileAsync(int layer, int zoom, int x, int y, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(
                string.Create(CultureInfo.InvariantCulture, $"api/tile/{layer}/{zoom}/{x}/{y}"), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes)
            {
                return null;
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new NavVfrTile(bytes, response.Content.Headers.ContentType?.MediaType ?? "image/png");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogDebug(ex, "NavVFR's tile did not come.");
            return null;
        }
    }

    private async Task<byte[]?> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes)
            {
                return null;
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length <= MaximumBytes ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // NavVFR is not running: the map tab says so. Not worth more than a debug line.
            _logger.LogDebug(ex, "NavVFR's feed did not answer.");
            return null;
        }
    }
}
