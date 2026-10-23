using StormATC.Domain.Atc;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Approach;

/// <summary>How a contact is flying relative to the aircraft that asks: toward it, across it, or away from it.</summary>
public enum TacticalAspect
{
    /// <summary>Pointing at the aircraft: the contact's nose is toward it.</summary>
    Hot,

    /// <summary>Flying across the aircraft's line of sight.</summary>
    Flank,

    /// <summary>Pointing away from the aircraft.</summary>
    Cold,
}

/// <summary>A contact as the tactical picture words it: bearing, range, altitude, aspect.</summary>
/// <param name="BearingMagnetic">The magnetic bearing from the aircraft to the contact, 1 to 360.</param>
/// <param name="RangeNauticalMiles">The distance in nautical miles, rounded.</param>
/// <param name="AltitudeFeet">The altitude, rounded to 100 ft.</param>
/// <param name="Aspect">How the contact is flying relative to the aircraft.</param>
/// <param name="Type">The simulator's title for the contact (the aircraft type the tablet generated), or empty.</param>
public sealed record TacticalContact(int BearingMagnetic, int RangeNauticalMiles, int AltitudeFeet, TacticalAspect Aspect, string Type);

/// <summary>Computes a tactical picture from the aircraft and the traffic around it.</summary>
public static class TacticalGeometry
{
    /// <summary>
    /// A contact seen from the aircraft. The simulator gives magnetic headings; the magnetic variation is taken from
    /// the aircraft itself (true heading less magnetic heading), the same one that turns bearings to magnetic.
    /// </summary>
    /// <param name="ownPosition">The aircraft's position.</param>
    /// <param name="ownTrueHeadingDegrees">The aircraft's true heading.</param>
    /// <param name="ownMagneticHeadingDegrees">The aircraft's magnetic heading.</param>
    /// <param name="contact">The contact.</param>
    public static TacticalContact Describe(
        GeoPosition ownPosition,
        double ownTrueHeadingDegrees,
        double ownMagneticHeadingDegrees,
        TrafficContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        double variation = Normalize180(ownTrueHeadingDegrees - ownMagneticHeadingDegrees);
        double trueBearing = ownPosition.TrueBearingTo(contact.Position).Degrees;
        int magneticBearing = Heading.FromDegrees(trueBearing - variation).Degrees;

        // Where the contact points, against where the aircraft is as seen from the contact.
        TacticalAspect aspect = TacticalAspect.Flank;
        if (contact.HeadingDegrees is double magneticHeading)
        {
            double contactTrue = magneticHeading + variation;
            double toOwn = contact.Position.TrueBearingTo(ownPosition).Degrees;
            double off = Math.Abs(Normalize180(contactTrue - toOwn));
            aspect = off <= 45 ? TacticalAspect.Hot : off >= 135 ? TacticalAspect.Cold : TacticalAspect.Flank;
        }

        return new TacticalContact(
            magneticBearing == 0 ? 360 : magneticBearing,
            (int)Math.Round(ownPosition.DistanceNauticalMilesTo(contact.Position)),
            (int)(Math.Round(contact.AltitudeFeet / 100.0) * 100),
            aspect,
            contact.ModelTitle);
    }

    private static double Normalize180(double degrees)
    {
        double value = degrees % 360;
        return value > 180 ? value - 360 : value <= -180 ? value + 360 : value;
    }
}
