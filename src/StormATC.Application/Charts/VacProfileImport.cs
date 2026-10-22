using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using StormATC.Application.Localization;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Charts;

/// <summary>What the model reads off a chart, one fact at a time with the words it relied on.</summary>
/// <remarks>
/// A model's reading of a chart is a proposal. Every value comes with the text it was read from and the
/// page it is on, so that the pilot can check each one against the chart before it becomes a profile.
/// </remarks>
public sealed record VacExtraction
{
    /// <summary>The aerodrome's ICAO code.</summary>
    public string Icao { get; init; } = string.Empty;

    /// <summary>The aerodrome's name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The runway direction the chart prefers, in degrees magnetic.</summary>
    public VacNumberFact? PreferredQfu { get; init; }

    /// <summary>The circuit height in feet above mean sea level.</summary>
    public VacNumberFact? CircuitHeightFeetAmsl { get; init; }

    /// <summary>The side of the runways the circuit lies on.</summary>
    public VacSideFact? CircuitSide { get; init; }

    /// <summary>The VFR reporting points.</summary>
    public IReadOnlyList<VacPointFact> ReportingPoints { get; init; } = [];
}

/// <summary>A number read off a chart.</summary>
public sealed record VacNumberFact
{
    /// <summary>The value, or null when the chart does not give one.</summary>
    public int? Value { get; init; }

    /// <summary>The words of the chart the value was read from, quoted.</summary>
    public string Evidence { get; init; } = string.Empty;

    /// <summary>The page the words are on, from 1.</summary>
    public int? Page { get; init; }

    /// <summary><c>high</c> when the chart states it plainly, <c>low</c> when it is inferred or hard to read.</summary>
    public string Confidence { get; init; } = "low";
}

/// <summary>The side of the runways a circuit lies on.</summary>
public sealed record VacSideFact
{
    /// <summary><c>north</c>, <c>south</c>, <c>east</c> or <c>west</c> of the runways; null when the chart does not say.</summary>
    public string? Side { get; init; }

    /// <summary>The words or the drawing the side was read from.</summary>
    public string Evidence { get; init; } = string.Empty;

    /// <summary>The page.</summary>
    public int? Page { get; init; }

    /// <summary>How sure the reading is.</summary>
    public string Confidence { get; init; } = "low";
}

/// <summary>A reporting point read off a chart.</summary>
public sealed record VacPointFact
{
    /// <summary>The designator on the chart, such as <c>E</c> or <c>NH</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>How a French controller says it.</summary>
    public string SpokenFrench { get; init; } = string.Empty;

    /// <summary>How an English controller says it.</summary>
    public string SpokenEnglish { get; init; } = string.Empty;

    /// <summary>Latitude in decimal degrees, when the chart gives coordinates.</summary>
    public double? Latitude { get; init; }

    /// <summary>Longitude in decimal degrees, when the chart gives coordinates.</summary>
    public double? Longitude { get; init; }

    /// <summary>The words of the chart the point was read from.</summary>
    public string Evidence { get; init; } = string.Empty;

    /// <summary>The page.</summary>
    public int? Page { get; init; }

    /// <summary>How sure the reading is.</summary>
    public string Confidence { get; init; } = "low";
}

/// <summary>The kind of a fact in a draft profile.</summary>
public enum VacFactKind
{
    /// <summary>The preferred runway direction.</summary>
    PreferredQfu,

    /// <summary>The circuit height.</summary>
    CircuitHeight,

    /// <summary>The side of the circuit.</summary>
    CircuitSide,

    /// <summary>A reporting point.</summary>
    ReportingPoint,
}

/// <summary>One fact of a draft profile, with what it was read from.</summary>
/// <param name="Kind">What the fact is.</param>
/// <param name="Label">Its name, such as "Preferred runway".</param>
/// <param name="Value">Its value as shown, such as <c>056°</c>.</param>
/// <param name="Evidence">The words of the chart it was read from.</param>
/// <param name="Page">The page, or null.</param>
/// <param name="NeedsReview">True when the model was not sure: the pilot checks it before keeping it.</param>
/// <param name="Number">The number, for a QFU, a height or a side bearing.</param>
/// <param name="Point">The reporting point, for a point.</param>
public sealed record VacProfileFact(
    VacFactKind Kind,
    string Label,
    string Value,
    string Evidence,
    int? Page,
    bool NeedsReview,
    int? Number = null,
    VacPointFact? Point = null);

/// <summary>A profile as the model proposed it, ready for the pilot to check.</summary>
/// <param name="Icao">The aerodrome.</param>
/// <param name="Name">Its name.</param>
/// <param name="ChartName">The chart file's name.</param>
/// <param name="Facts">The facts, each with its evidence.</param>
/// <param name="Warnings">What was left out or changed, and why.</param>
public sealed record VacProfileDraft(
    string Icao,
    string Name,
    string ChartName,
    IReadOnlyList<VacProfileFact> Facts,
    IReadOnlyList<string> Warnings);

/// <summary>The outcome of reading a chart.</summary>
public sealed record VacImportResult
{
    /// <summary>Whether a draft was made.</summary>
    public required bool Success { get; init; }

    /// <summary>The draft, when it was made.</summary>
    public VacProfileDraft? Draft { get; init; }

    /// <summary>Why it was not, or a note on how it went.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>How many pages were sent to the model.</summary>
    public int PagesSent { get; init; }

    /// <summary>The model provider that read the chart.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Tokens used, where the provider says.</summary>
    public int? Tokens { get; init; }
}

/// <summary>Where a profile comes from.</summary>
public enum VacProfileOrigin
{
    /// <summary>There is none.</summary>
    None,

    /// <summary>It came with StormATC.</summary>
    Bundled,

    /// <summary>The pilot saved it.</summary>
    User,
}

/// <summary>Keeps the profiles the pilot confirmed.</summary>
public interface IVacProfileStore
{
    /// <summary>Where a profile for an aerodrome comes from today.</summary>
    /// <param name="icao">The aerodrome.</param>
    VacProfileOrigin Origin(string icao);

    /// <summary>Saves a profile in the pilot's own folder, which wins over the bundled one.</summary>
    /// <param name="icao">The aerodrome.</param>
    /// <param name="profileJson">The profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file written.</returns>
    Task<string> SaveAsync(string icao, string profileJson, CancellationToken cancellationToken);
}

/// <summary>Turns what a model read into a draft the pilot can check, and a draft into a profile.</summary>
public static partial class VacProfileDraftBuilder
{
    private const int MaximumPoints = 12;

    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Checks a model's reading of a chart. Nothing is taken on trust: a value outside what a chart can
    /// say is dropped, a value with no quoted words is dropped, and a value the model was unsure of is
    /// marked for the pilot to check.
    /// </summary>
    /// <param name="extraction">What the model read.</param>
    /// <param name="chart">The chart that was read.</param>
    public static VacImportResult Build(VacExtraction extraction, VacChart chart)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(chart);

        List<string> warnings = [];
        string icao = (extraction.Icao ?? string.Empty).Trim().ToUpperInvariant();
        if (chart.Icao is { Length: 4 } named)
        {
            if (icao != named)
            {
                warnings.Add(UiText.T($"The model named the aerodrome {icao}; the chart's name says {named}, which is used."));
            }

            icao = named;
        }

        if (!Icao().IsMatch(icao))
        {
            return new VacImportResult
            {
                Success = false,
                Message = UiText.T("The model did not find the aerodrome's ICAO code. Name the file after it, such as AD-2.LFPL.pdf."),
            };
        }

        List<VacProfileFact> facts = [];

        if (Number(extraction.PreferredQfu, 1, 360, UiText.T("Preferred runway"), warnings) is { } qfu)
        {
            facts.Add(new VacProfileFact(
                VacFactKind.PreferredQfu,
                UiText.T("Preferred runway"),
                string.Create(CultureInfo.InvariantCulture, $"{qfu.Value:000}°"),
                qfu.Evidence,
                qfu.Page,
                qfu.NeedsReview,
                Number: qfu.Value));
        }

        if (Number(extraction.CircuitHeightFeetAmsl, 300, 8000, UiText.T("Circuit height"), warnings) is { } height)
        {
            facts.Add(new VacProfileFact(
                VacFactKind.CircuitHeight,
                UiText.T("Circuit height"),
                string.Create(CultureInfo.InvariantCulture, $"{height.Value} ft AMSL"),
                height.Evidence,
                height.Page,
                height.NeedsReview,
                Number: height.Value));
        }

        if (Side(extraction.CircuitSide, warnings) is { } side)
        {
            facts.Add(side);
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (VacPointFact point in extraction.ReportingPoints ?? [])
        {
            if (facts.Count(f => f.Kind == VacFactKind.ReportingPoint) >= MaximumPoints)
            {
                warnings.Add(UiText.T($"Only the first {MaximumPoints} reporting points are kept."));
                break;
            }

            if (Point(point, seen, warnings) is { } fact)
            {
                facts.Add(fact);
            }
        }

        if (facts.Count == 0)
        {
            return new VacImportResult
            {
                Success = false,
                Message = UiText.T("The model found nothing usable on this chart: no preferred runway, circuit height, circuit side or reporting point."),
            };
        }

        string name = (extraction.Name ?? string.Empty).Trim();
        return new VacImportResult
        {
            Success = true,
            Draft = new VacProfileDraft(icao, name.Length > 80 ? name[..80] : name, chart.Name, facts, warnings),
        };
    }

    /// <summary>
    /// Writes the profile file for the facts the pilot kept. The file has the shape of the profiles
    /// that come with StormATC, plus where each value was read from.
    /// </summary>
    /// <param name="draft">The draft.</param>
    /// <param name="keep">The facts to keep.</param>
    /// <param name="provider">The provider that read the chart.</param>
    /// <param name="importedAt">When the pilot confirmed it.</param>
    public static string ToProfileJson(
        VacProfileDraft draft,
        IEnumerable<VacProfileFact> keep,
        string provider,
        DateTimeOffset importedAt)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(keep);

        List<VacProfileFact> kept = [.. keep];
        var root = new Dictionary<string, object?>
        {
            ["$comment"] = new[]
            {
                $"Read from the chart {draft.ChartName} by a language model ({provider}) and confirmed fact by fact by the pilot.",
                "Check it against the chart: a model can misread a table or a drawing.",
                "Flight simulation only: never use for real navigation.",
            },
            ["icao"] = draft.Icao,
            ["name"] = draft.Name,
            ["source"] = new Dictionary<string, object?>
            {
                ["publication"] = $"Chart {draft.ChartName}",
                ["importedBy"] = provider,
                ["importedAt"] = importedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
        };

        VacProfileFact? qfu = kept.FirstOrDefault(f => f.Kind == VacFactKind.PreferredQfu);
        if (qfu?.Number is int preferred)
        {
            root["preferredQfu"] = preferred;
            root["preferredQfuEvidence"] = qfu.Evidence;
        }

        Dictionary<string, object?> circuit = [];
        VacProfileFact? height = kept.FirstOrDefault(f => f.Kind == VacFactKind.CircuitHeight);
        if (height?.Number is int feet)
        {
            circuit["heightFeetAmsl"] = feet;
            circuit["heightEvidence"] = height.Evidence;
        }

        VacProfileFact? side = kept.FirstOrDefault(f => f.Kind == VacFactKind.CircuitSide);
        if (side?.Number is int bearing)
        {
            circuit["sideBearingDegrees"] = bearing;
            circuit["sideEvidence"] = side.Evidence;
        }

        if (circuit.Count > 0)
        {
            root["circuit"] = circuit;
        }

        List<object> points = [];
        foreach (VacProfileFact fact in kept.Where(f => f.Kind == VacFactKind.ReportingPoint && f.Point is not null))
        {
            VacPointFact point = fact.Point!;
            var entry = new Dictionary<string, object?>
            {
                ["name"] = point.Name,
                ["spoken"] = new Dictionary<string, string> { ["fr"] = point.SpokenFrench, ["en"] = point.SpokenEnglish },
            };
            if (point.Latitude is double latitude && point.Longitude is double longitude)
            {
                entry["latitude"] = latitude;
                entry["longitude"] = longitude;
            }

            entry["evidence"] = point.Evidence;
            points.Add(entry);
        }

        if (points.Count > 0)
        {
            root["reportingPoints"] = points;
        }

        return JsonSerializer.Serialize(root, ProfileJsonOptions);
    }

    private sealed record Checked(int Value, string Evidence, int? Page, bool NeedsReview);

    private static Checked? Number(VacNumberFact? fact, int minimum, int maximum, string label, List<string> warnings)
    {
        if (fact?.Value is not int value)
        {
            return null;
        }

        if (value < minimum || value > maximum)
        {
            warnings.Add(UiText.T($"{label}: {value} is not a value a chart gives; it was left out."));
            return null;
        }

        string evidence = (fact.Evidence ?? string.Empty).Trim();
        if (evidence.Length == 0)
        {
            warnings.Add(UiText.T($"{label}: the model quoted no words from the chart; it was left out."));
            return null;
        }

        return new Checked(value, evidence, fact.Page, NeedsReview(fact.Confidence));
    }

    private static VacProfileFact? Side(VacSideFact? fact, List<string> warnings)
    {
        int? bearing = (fact?.Side ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "north" => 0,
            "east" => 90,
            "south" => 180,
            "west" => 270,
            _ => null,
        };

        if (bearing is null)
        {
            return null;
        }

        string evidence = (fact!.Evidence ?? string.Empty).Trim();
        if (evidence.Length == 0)
        {
            warnings.Add(UiText.T("Circuit side: the model gave no words or drawing it relied on; it was left out."));
            return null;
        }

        string shown = fact.Side!.Trim().ToLowerInvariant() switch
        {
            "north" => UiText.T("North of the runways"),
            "east" => UiText.T("East of the runways"),
            "south" => UiText.T("South of the runways"),
            _ => UiText.T("West of the runways"),
        };

        // The side is often read off a drawing: always worth a look.
        return new VacProfileFact(
            VacFactKind.CircuitSide,
            UiText.T("Circuit side"),
            shown,
            evidence,
            fact.Page,
            NeedsReview: true,
            Number: bearing);
    }

    private static VacProfileFact? Point(VacPointFact point, HashSet<string> seen, List<string> warnings)
    {
        string name = (point.Name ?? string.Empty).Trim().ToUpperInvariant();
        if (!PointName().IsMatch(name))
        {
            warnings.Add(UiText.T($"A reporting point named '{point.Name}' was left out: a designator has one to six letters or digits."));
            return null;
        }

        if (!seen.Add(name))
        {
            return null;
        }

        string evidence = (point.Evidence ?? string.Empty).Trim();
        if (evidence.Length == 0)
        {
            warnings.Add(UiText.T($"Reporting point {name}: the model quoted no words from the chart; it was left out."));
            return null;
        }

        string french = Spoken(point.SpokenFrench, name);
        string english = Spoken(point.SpokenEnglish, name);

        double? latitude = null;
        double? longitude = null;
        if (point.Latitude is double lat && point.Longitude is double lon)
        {
            if (GeoPosition.TryCreate(lat, lon, out _))
            {
                latitude = Math.Round(lat, 5);
                longitude = Math.Round(lon, 5);
            }
            else
            {
                warnings.Add(UiText.T($"Reporting point {name}: its coordinates were not valid and were dropped."));
            }
        }

        return new VacProfileFact(
            VacFactKind.ReportingPoint,
            UiText.T($"Reporting point {name}"),
            french == english ? french : $"{french} / {english}",
            evidence,
            point.Page,
            NeedsReview(point.Confidence),
            Point: point with
            {
                Name = name,
                SpokenFrench = french,
                SpokenEnglish = english,
                Latitude = latitude,
                Longitude = longitude,
                Evidence = evidence,
            });
    }

    private static string Spoken(string? spoken, string fallback)
    {
        string text = (spoken ?? string.Empty).Trim();
        return text.Length == 0 || text.Length > 40 ? fallback : text;
    }

    private static bool NeedsReview(string? confidence) =>
        !string.Equals((confidence ?? string.Empty).Trim(), "high", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Z]{4}$")]
    private static partial Regex Icao();

    [GeneratedRegex("^[A-Z0-9]{1,6}$")]
    private static partial Regex PointName();
}
