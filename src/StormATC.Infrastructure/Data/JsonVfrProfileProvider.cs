using System.Text.Json;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Domain.Airports;

namespace StormATC.Infrastructure.Data;

/// <summary>
/// Loads the VFR profile of an aerodrome from <c>data/vfr-profiles/&lt;ICAO&gt;.json</c>.
/// </summary>
/// <remarks>
/// A missing file is normal: most aerodromes have no profile and the engine then uses generic
/// phraseology. A malformed file is logged and treated as missing, never thrown.
/// </remarks>
public sealed class JsonVfrProfileProvider(
    IStoragePaths storagePaths,
    ILogger<JsonVfrProfileProvider> logger) : IVfrProfileProvider
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <inheritdoc />
    public async Task<VfrAerodromeProfile?> GetProfileAsync(string icao, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(icao) || icao.Any(c => !char.IsLetterOrDigit(c)))
        {
            return null;
        }

        string path = Path.Combine(
            storagePaths.DataDirectory,
            "vfr-profiles",
            $"{icao.ToUpperInvariant()}.json");

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            using JsonDocument document = await JsonDocument
                .ParseAsync(stream, DocumentOptions, cancellationToken)
                .ConfigureAwait(false);
            return Parse(document.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            logger.LogError(ex, "The VFR profile of {Icao} is invalid and was not loaded.", icao);
            return null;
        }
    }

    /// <summary>Reads a profile document. Only the fields the engine uses are taken.</summary>
    internal static VfrAerodromeProfile? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("icao", out JsonElement icaoElement)
            || icaoElement.GetString() is not { Length: > 0 } icao)
        {
            return null;
        }

        var points = new List<VfrReportingPoint>();
        if (root.TryGetProperty("reportingPoints", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                string? name = item.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string french = name;
                string english = name;
                if (item.TryGetProperty("spoken", out JsonElement spoken) && spoken.ValueKind == JsonValueKind.Object)
                {
                    french = SpokenName(spoken, "fr") ?? name;
                    english = SpokenName(spoken, "en") ?? name;
                }

                points.Add(new VfrReportingPoint(name.Trim(), french, english));
            }
        }

        int? circuitHeight = null;
        int? sideBearing = null;
        if (root.TryGetProperty("circuit", out JsonElement circuit) && circuit.ValueKind == JsonValueKind.Object)
        {
            circuitHeight = Integer(circuit, "heightFeetAmsl");
            sideBearing = Integer(circuit, "sideBearingDegrees");
        }

        return new VfrAerodromeProfile
        {
            Icao = icao.ToUpperInvariant(),
            Name = root.TryGetProperty("name", out JsonElement nameValue) ? nameValue.GetString() ?? string.Empty : string.Empty,
            ReportingPoints = points,
            CircuitHeightFeetAmsl = circuitHeight,
            PreferredQfu = Integer(root, "preferredQfu"),
            CircuitSideBearing = sideBearing,
        };
    }

    private static string? SpokenName(JsonElement spoken, string language) =>
        spoken.TryGetProperty(language, out JsonElement value) && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static int? Integer(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : null;
}
