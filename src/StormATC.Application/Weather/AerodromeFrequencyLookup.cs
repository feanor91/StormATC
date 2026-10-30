using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Weather;

/// <summary>An aerodrome service that uses a frequency.</summary>
/// <param name="Icao">The aerodrome.</param>
/// <param name="Name">Its name.</param>
/// <param name="Service">The service, as the chart labels it: ATIS, TWR, APP, A/A...</param>
public sealed record AerodromeFrequencyMatch(string Icao, string Name, string Service);

/// <summary>Finds aerodromes and services from the frequencies printed on the VAC charts.</summary>
public static class AerodromeFrequencyLookup
{
    /// <summary>The label the charts use for an ATIS.</summary>
    public const string AtisService = "ATIS";

    /// <summary>Lists the services that use a frequency.</summary>
    /// <param name="profiles">The profiles to look in.</param>
    /// <param name="frequency">The frequency.</param>
    public static IReadOnlyList<AerodromeFrequencyMatch> FindByFrequency(
        IEnumerable<VfrAerodromeProfile> profiles,
        Frequency frequency)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return
        [
            .. profiles
                .SelectMany(
                    profile => profile.Frequencies
                        .Where(f => f.Frequency.IsSameChannel(frequency))
                        .Select(f => new AerodromeFrequencyMatch(profile.Icao, profile.Name, f.Service)))
                .OrderBy(match => match.Icao, StringComparer.Ordinal),
        ];
    }

    /// <summary>The ATIS frequency of an aerodrome, when its chart prints one.</summary>
    /// <param name="profile">The aerodrome's profile.</param>
    public static Frequency? AtisOf(VfrAerodromeProfile? profile)
    {
        AerodromeFrequency? atis = profile?.Frequencies.FirstOrDefault(
            f => f.Service == AtisService);
        return atis?.Frequency;
    }

    /// <summary>The frequencies of an aerodrome on one line, for example <c>ATIS 123.130 · TWR 118.455</c>.</summary>
    /// <param name="profile">The aerodrome's profile.</param>
    public static string Summarize(VfrAerodromeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return string.Join(
            " · ",
            profile.Frequencies.Select(f => $"{f.Service} {f.Frequency.ToDisplay()}"));
    }
}
