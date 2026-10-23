using StormATC.Domain.Aircraft;
using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Approach;

/// <summary>The radar approach a military controller talks the aircraft down.</summary>
public enum RadarApproachKind
{
    /// <summary>Precision approach radar (PAR, GCA): azimuth and glidepath guidance.</summary>
    Par,

    /// <summary>Surveillance radar approach (SRA): azimuth guidance and a recommended altitude each mile.</summary>
    Sra,
}

/// <summary>Where the aircraft is, laterally, against the extended runway centreline.</summary>
public enum RadarLateral
{
    /// <summary>On the centreline.</summary>
    OnCentreline,

    /// <summary>Slightly left of the centreline.</summary>
    SlightlyLeft,

    /// <summary>Slightly right of the centreline.</summary>
    SlightlyRight,

    /// <summary>Left of the centreline.</summary>
    Left,

    /// <summary>Right of the centreline.</summary>
    Right,
}

/// <summary>Where the aircraft is, vertically, against the glidepath.</summary>
public enum RadarVertical
{
    /// <summary>On the glidepath.</summary>
    OnPath,

    /// <summary>Slightly above the glidepath.</summary>
    SlightlyAbove,

    /// <summary>Slightly below the glidepath.</summary>
    SlightlyBelow,

    /// <summary>Above the glidepath.</summary>
    Above,

    /// <summary>Below the glidepath.</summary>
    Below,
}

/// <summary>
/// A radar approach in progress, kept in the session so the controller keeps talking the aircraft down.
/// </summary>
public sealed record RadarApproachState
{
    /// <summary>PAR or SRA.</summary>
    public required RadarApproachKind Kind { get; init; }

    /// <summary>The runway the approach is flown to.</summary>
    public required RunwayIdentifier Runway { get; init; }

    /// <summary>When the controller accepted the request.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the controller last spoke.</summary>
    public required DateTimeOffset LastCallAt { get; init; }

    /// <summary>True once the aircraft is inside the approach wedge and radar contact is established.</summary>
    public bool Established { get; init; }

    /// <summary>True once the controller has asked for the gear check.</summary>
    public bool GearChecked { get; init; }

    /// <summary>True once the controller has stopped calling: the end of the approach was announced.</summary>
    public bool Completed { get; init; }
}

/// <summary>What the controller says at one call: lateral, vertical, distance, heading, altitude.</summary>
/// <param name="Lateral">Position against the centreline.</param>
/// <param name="Vertical">Position against the glidepath (PAR only).</param>
/// <param name="DistanceNauticalMiles">Distance to the threshold, in whole miles.</param>
/// <param name="CorrectionHeading">Magnetic heading to regain the centreline, when the aircraft is clearly off it.</param>
/// <param name="RecommendedAltitudeFeet">Altitude recommended at this distance (SRA only).</param>
public sealed record RadarCall(
    RadarLateral Lateral,
    RadarVertical? Vertical,
    int DistanceNauticalMiles,
    int? CorrectionHeading,
    int? RecommendedAltitudeFeet);

/// <summary>Where the aircraft is against the approach path of a runway.</summary>
/// <param name="AlongTrackNauticalMiles">Distance to the threshold along the extended centreline; positive before it.</param>
/// <param name="CrossTrackMeters">Distance from the extended centreline; positive when the aircraft is left of it.</param>
/// <param name="GlideDeviationFeet">Height above the glidepath; negative when below it.</param>
/// <param name="RecommendedAltitudeFeet">The altitude of the glidepath at this distance, above sea level.</param>
/// <param name="RunwayMagneticHeading">The runway heading, magnetic.</param>
/// <param name="InWedge">True when the aircraft is on the approach side of the threshold and within the wedge.</param>
public readonly record struct RadarGeometry(
    double AlongTrackNauticalMiles,
    double CrossTrackMeters,
    double GlideDeviationFeet,
    int RecommendedAltitudeFeet,
    int RunwayMagneticHeading,
    bool InWedge)
{
    /// <summary>The glide angle of a precision approach, in degrees.</summary>
    public const double GlideAngleDegrees = 3.0;

    /// <summary>The height of the glidepath over the threshold, in feet.</summary>
    public const double ThresholdCrossingHeightFeet = 50;

    /// <summary>The furthest distance at which radar guidance starts, in nautical miles.</summary>
    public const double MaximumDistanceNauticalMiles = 12;

    /// <summary>The widest angle from the extended centreline that is still inside the wedge, in degrees.</summary>
    public const double WedgeDegrees = 30;

    private const double FeetPerNauticalMile = 6076.12;
    private const double MetersPerNauticalMile = 1852;

    /// <summary>
    /// Places an aircraft against the approach path of a runway. The height is the aircraft's height above
    /// the ground, because the pilot may have set the QNH, the QFE or the standard pressure on the altimeter.
    /// </summary>
    /// <param name="runway">The runway the approach is flown to.</param>
    /// <param name="fieldElevationFeet">The elevation of the aerodrome.</param>
    /// <param name="aircraft">The aircraft.</param>
    public static RadarGeometry Compute(RunwayData runway, int fieldElevationFeet, AircraftState aircraft)
    {
        ArgumentNullException.ThrowIfNull(runway);
        ArgumentNullException.ThrowIfNull(aircraft);

        double runwayTrue = runway.CenterlineBearing.Degrees;
        double magneticVariation = Normalize180(aircraft.TrueHeadingDegrees - aircraft.MagneticHeadingDegrees);
        int runwayMagnetic = Heading.FromDegrees(runwayTrue - magneticVariation).Degrees;

        double distance = aircraft.Position.DistanceNauticalMilesTo(runway.ThresholdPosition);
        double bearingFromThreshold = runway.ThresholdPosition.TrueBearingTo(aircraft.Position).Degrees;
        double offAxis = Normalize180(bearingFromThreshold - (runwayTrue + 180));

        double along = distance * Math.Cos(offAxis * Math.PI / 180);
        double crossNauticalMiles = distance * Math.Sin(offAxis * Math.PI / 180);

        double height = aircraft.AltitudeAboveGroundFeet > 0
            ? aircraft.AltitudeAboveGroundFeet
            : aircraft.IndicatedAltitudeFeet - fieldElevationFeet;
        double pathHeight = ThresholdCrossingHeightFeet
            + (Math.Max(along, 0) * FeetPerNauticalMile * Math.Tan(GlideAngleDegrees * Math.PI / 180));

        bool inWedge = along > 0 && along <= MaximumDistanceNauticalMiles && Math.Abs(offAxis) <= WedgeDegrees;
        return new RadarGeometry(
            along,
            crossNauticalMiles * MetersPerNauticalMile,
            height - pathHeight,
            (int)(Math.Round((fieldElevationFeet + pathHeight) / 50.0) * 50),
            runwayMagnetic,
            inWedge);
    }

    /// <summary>Words a lateral position: within 100 m is on the centreline, within 300 m slightly off it.</summary>
    public RadarLateral Lateral => Math.Abs(CrossTrackMeters) switch
    {
        <= 100 => RadarLateral.OnCentreline,
        <= 300 => CrossTrackMeters > 0 ? RadarLateral.SlightlyLeft : RadarLateral.SlightlyRight,
        _ => CrossTrackMeters > 0 ? RadarLateral.Left : RadarLateral.Right,
    };

    /// <summary>Words a vertical position: within 50 ft is on the path, within 150 ft slightly off it.</summary>
    public RadarVertical Vertical => Math.Abs(GlideDeviationFeet) switch
    {
        <= 50 => RadarVertical.OnPath,
        <= 150 => GlideDeviationFeet > 0 ? RadarVertical.SlightlyAbove : RadarVertical.SlightlyBelow,
        _ => GlideDeviationFeet > 0 ? RadarVertical.Above : RadarVertical.Below,
    };

    /// <summary>
    /// The magnetic heading that brings the aircraft back to the centreline: the runway heading turned 10 degrees
    /// toward it, or 20 when the aircraft is more than 800 m off. Null when it is within 300 m of it.
    /// </summary>
    public int? CorrectionHeading
    {
        get
        {
            double offset = Math.Abs(CrossTrackMeters);
            if (offset <= 300)
            {
                return null;
            }

            int turn = offset > 800 ? 20 : 10;
            return Heading.FromDegrees(RunwayMagneticHeading + (CrossTrackMeters > 0 ? turn : -turn)).Degrees;
        }
    }

    private static double Normalize180(double degrees)
    {
        double value = degrees % 360;
        return value > 180 ? value - 360 : value <= -180 ? value + 360 : value;
    }
}
