using System.Text.Json;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Data;

/// <summary>
/// Loads the VFR profile of an aerodrome from <c>data/vfr-profiles/&lt;ICAO&gt;.json</c>, or from the same
/// file name in the user's <c>vfr-profiles</c> folder, which is read first.
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

        // The pilot's own profile (one read from a chart and confirmed) wins over the bundled one.
        string file = $"{icao.ToUpperInvariant()}.json";
        string? path = new[]
        {
            Path.Combine(storagePaths.ApplicationDataDirectory, "vfr-profiles", file),
            Path.Combine(storagePaths.DataDirectory, "vfr-profiles", file),
        }.FirstOrDefault(File.Exists);

        if (path is null)
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

        string? serviceKind = null;
        if (root.TryGetProperty("service", out JsonElement service) && service.ValueKind == JsonValueKind.Object
            && service.TryGetProperty("kind", out JsonElement kind) && kind.ValueKind == JsonValueKind.String)
        {
            serviceKind = kind.GetString();
        }

        Frequency? airAir = null;
        if (root.TryGetProperty("frequencies", out JsonElement frequencies) && frequencies.ValueKind == JsonValueKind.Object
            && frequencies.TryGetProperty("airAir", out JsonElement air) && air.ValueKind == JsonValueKind.Number
            && air.TryGetDecimal(out decimal megahertz) && Frequency.TryFromMegahertz(megahertz, out Frequency parsed))
        {
            airAir = parsed;
        }

        var listed = new List<AerodromeFrequency>();
        if (root.TryGetProperty("frequencies", out JsonElement all) && all.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty entry in all.EnumerateObject())
            {
                string label = ServiceLabel(entry.Name);
                if (entry.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in entry.Value.EnumerateArray())
                    {
                        AddFrequency(listed, label, item);
                    }
                }
                else
                {
                    AddFrequency(listed, label, entry.Value);
                }
            }
        }

        return new VfrAerodromeProfile
        {
            Frequencies = listed,
            ServiceKind = serviceKind,
            AirAirFrequency = airAir,
            Icao = icao.ToUpperInvariant(),
            Name = root.TryGetProperty("name", out JsonElement nameValue) ? nameValue.GetString() ?? string.Empty : string.Empty,
            ReportingPoints = points,
            CircuitHeightFeetAmsl = circuitHeight,
            PreferredQfu = Integer(root, "preferredQfu"),
            CircuitSideBearing = sideBearing,
        };
    }

    /// <summary>A frequency written as a number or as <c>{ "mhz": 122.1, "remark": "..." }</c>.</summary>
    private static void AddFrequency(List<AerodromeFrequency> into, string service, JsonElement value)
    {
        string remark = string.Empty;
        JsonElement number = value;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("mhz", out JsonElement mhz))
        {
            number = mhz;
            if (value.TryGetProperty("remark", out JsonElement note) && note.ValueKind == JsonValueKind.String)
            {
                remark = note.GetString() ?? string.Empty;
            }
        }

        if (number.ValueKind == JsonValueKind.Number && number.TryGetDecimal(out decimal megahertz)
            && Frequency.TryFromMegahertz(megahertz, out Frequency frequency))
        {
            into.Add(new AerodromeFrequency(service, frequency, remark));
        }
    }

    private static string ServiceLabel(string key) => key switch
    {
        "approach" or "app" => "APP",
        "approachTransit" => "APP (transit)",
        "tower" or "twr" => "TWR",
        "towerLineUpAndTakeOff" => "TWR (alignement, décollage)",
        "ground" or "gnd" => "GND",
        "atis" or "atisFr" or "atisEn" => "ATIS",
        "airAir" => "A/A",
        "afis" => "AFIS",
        "par" => "PAR",
        "sre" or "sra" => "SRE",
        _ => key.ToUpperInvariant(),
    };

    private static string? SpokenName(JsonElement spoken, string language) =>
        spoken.TryGetProperty(language, out JsonElement value) && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static int? Integer(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : null;
}
