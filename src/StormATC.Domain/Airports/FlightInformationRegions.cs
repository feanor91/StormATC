using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Airports;

/// <summary>One frequency of a flight information service (FIS), the "Information" a VFR pilot calls for a flight following.</summary>
/// <param name="ServiceName">The name spoken on the radio, for example <c>Paris Information</c>.</param>
/// <param name="Sector">The sector of the service this frequency belongs to (Nord, Sud, Ouest).</param>
/// <param name="Frequency">The sector's published frequency.</param>
public sealed record FlightInformationSector(string ServiceName, string Sector, Frequency Frequency);

/// <summary>
/// The flight information services of France and the frequencies they publish, with the part of the country each
/// one covers.
/// </summary>
/// <remarks>
/// <para>
/// The frequencies are those of the French AIP (GEN 3.4, "Moyens de radiocommunication des ACC/UAC", service FIS):
/// Paris Information on 125.700 (Nord), 126.100 (Sud) and 129.625 (Ouest), Marseille Information on 124.500 (Nord)
/// and 120.550 (Sud). The AIP publishes no flight information service frequency for the Brest, Bordeaux and Reims
/// area controls, so there is none to find there.
/// </para>
/// <para>
/// The AIP draws the limits of the sectors on a chart (ENR 6.1), not in a table, so the limits here are an
/// approximation by latitude and longitude, good enough to tell which service a flight in France would call. They are
/// not a source for real navigation.
/// </para>
/// </remarks>
public static class FlightInformationRegions
{
    /// <summary>Paris Information, northern sector.</summary>
    public static readonly FlightInformationSector ParisNord = new("Paris Information", "Nord", Frequency.FromMegahertz(125.700m));

    /// <summary>Paris Information, southern sector.</summary>
    public static readonly FlightInformationSector ParisSud = new("Paris Information", "Sud", Frequency.FromMegahertz(126.100m));

    /// <summary>Paris Information, western sector.</summary>
    public static readonly FlightInformationSector ParisOuest = new("Paris Information", "Ouest", Frequency.FromMegahertz(129.625m));

    /// <summary>Marseille Information, northern sector.</summary>
    public static readonly FlightInformationSector MarseilleNord = new("Marseille Information", "Nord", Frequency.FromMegahertz(124.500m));

    /// <summary>Marseille Information, southern sector.</summary>
    public static readonly FlightInformationSector MarseilleSud = new("Marseille Information", "Sud", Frequency.FromMegahertz(120.550m));

    /// <summary>Every published flight information frequency.</summary>
    public static IReadOnlyList<FlightInformationSector> All { get; } =
        [ParisNord, ParisSud, ParisOuest, MarseilleNord, MarseilleSud];

    /// <summary>
    /// The flight information sector that serves a position, or null when no service is published there (the west and
    /// south-west of the country, and outside France).
    /// </summary>
    /// <param name="position">Where the aircraft is.</param>
    public static FlightInformationSector? Find(GeoPosition position)
    {
        double latitude = position.Latitude;
        double longitude = position.Longitude;

        // Outside metropolitan France.
        if (latitude is < 41.0 or > 51.5 || longitude is < -5.5 or > 10.0)
        {
            return null;
        }

        // Corsica and the south-east: Marseille.
        if (longitude >= 8.0 && latitude < 43.5)
        {
            return MarseilleSud;
        }

        if (longitude >= 2.5 && latitude < 46.3)
        {
            return latitude >= 44.3 ? MarseilleNord : MarseilleSud;
        }

        // South-west (Bordeaux) and Brittany (Brest): no flight information service is published.
        if (latitude < 46.3 || longitude < -0.6)
        {
            return null;
        }

        if (longitude < 1.2)
        {
            return ParisOuest;
        }

        return latitude >= 48.9 || (longitude >= 4.3 && latitude >= 48.0) ? ParisNord : ParisSud;
    }

    /// <summary>The sector that publishes a frequency, or null when the frequency is not a flight information one.</summary>
    /// <param name="frequency">The frequency the radio is tuned to.</param>
    public static FlightInformationSector? FindByFrequency(Frequency frequency)
    {
        foreach (FlightInformationSector sector in All)
        {
            if (sector.Frequency.IsSameChannel(frequency))
            {
                return sector;
            }
        }

        return null;
    }
}
