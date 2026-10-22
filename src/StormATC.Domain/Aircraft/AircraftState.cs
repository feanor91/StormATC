using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Aircraft;

/// <summary>
/// An immutable snapshot of the aircraft as reported by the simulator.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>only</b> source of physical truth in StormATC. The ATC engine reads it to
/// decide what is possible; the language model never writes it and never sees it as something
/// it may contradict. If a pilot says "we're airborne" and this says otherwise, this wins.
/// </para>
/// <para>
/// Snapshots are records so that a decision made at time T can hold the exact state it was
/// made against, which matters for both diagnostics and replay.
/// </para>
/// </remarks>
public sealed record AircraftState
{
    /// <summary>When this snapshot was taken.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Aircraft position.</summary>
    public required GeoPosition Position { get; init; }

    /// <summary>Indicated altitude in feet, referenced to the current altimeter setting.</summary>
    public required double IndicatedAltitudeFeet { get; init; }

    /// <summary>Pressure altitude in feet, referenced to the standard datum.</summary>
    public required double PressureAltitudeFeet { get; init; }

    /// <summary>Ground speed in knots.</summary>
    public required double GroundSpeedKnots { get; init; }

    /// <summary>Indicated airspeed in knots.</summary>
    public required double IndicatedAirspeedKnots { get; init; }

    /// <summary>True heading in degrees.</summary>
    public required double TrueHeadingDegrees { get; init; }

    /// <summary>Magnetic heading in degrees — the one used on the radio.</summary>
    public required double MagneticHeadingDegrees { get; init; }

    /// <summary>Vertical speed in feet per minute. Positive is a climb.</summary>
    public required double VerticalSpeedFeetPerMinute { get; init; }

    /// <summary>True when the simulator reports weight on wheels.</summary>
    public required bool IsOnGround { get; init; }

    /// <summary>True when the parking brake is set.</summary>
    public required bool ParkingBrakeSet { get; init; }

    /// <summary>True when at least one engine is running.</summary>
    public required bool EnginesRunning { get; init; }

    /// <summary>The aircraft title string reported by the simulator.</summary>
    public required string AircraftTitle { get; init; }

    /// <summary>The callsign or registration reported by the simulator, when available.</summary>
    public string AtcIdentifier { get; init; } = string.Empty;

    /// <summary>
    /// True when the pilot is in the aircraft of a running simulator. False in the simulator's menus
    /// (the data it reports there is meaningless) and for the scripted aircraft, which has no pilot
    /// in a simulator. Nothing that reads the radio aloud should start before it is true.
    /// </summary>
    public bool IsUserInAircraft { get; init; } = true;

    /// <summary>Height above the surface directly below the aircraft, in feet.</summary>
    public double AltitudeAboveGroundFeet { get; init; }

    /// <summary>True when the autopilot master is engaged.</summary>
    public bool AutopilotEngaged { get; init; }

    /// <summary>True when the landing-gear handle is in the down position.</summary>
    public bool GearHandleDown { get; init; } = true;

    /// <summary>The currently selected flap-handle detent.</summary>
    public int FlapsHandleIndex { get; init; }

    /// <summary>COM radio state.</summary>
    public required RadioState Radio { get; init; }

    /// <summary>Transponder state.</summary>
    public required TransponderState Transponder { get; init; }

    /// <summary>Magnetic heading as a validated value object.</summary>
    public Heading MagneticHeading => Heading.FromDegrees(MagneticHeadingDegrees);

    /// <summary>Indicated altitude rounded to the nearest foot, clamped to the valid range.</summary>
    public int IndicatedAltitudeRounded => (int)Math.Round(
        Math.Clamp(IndicatedAltitudeFeet, Altitude.MinimumFeet, Altitude.MaximumFeet),
        MidpointRounding.AwayFromZero);

    /// <summary>
    /// True when the aircraft is stationary enough to be considered parked or holding.
    /// </summary>
    public bool IsStationary => IsOnGround && GroundSpeedKnots < 1.0;

    /// <summary>
    /// True when the aircraft is taxiing: on the ground and moving, but well below any
    /// plausible rotation speed.
    /// </summary>
    public bool IsTaxiing => IsOnGround && GroundSpeedKnots is >= 1.0 and < 40.0;

    /// <summary>
    /// True when the aircraft is established in a climb rather than merely bouncing.
    /// The 300 fpm threshold filters out ground-handling noise and light turbulence.
    /// </summary>
    public bool IsClimbing => !IsOnGround && VerticalSpeedFeetPerMinute > 300;

    /// <summary>True when established in a descent.</summary>
    public bool IsDescending => !IsOnGround && VerticalSpeedFeetPerMinute < -300;

    /// <summary>
    /// Creates a neutral state used before any simulator data has arrived, so that the UI and
    /// engine never have to reason about a null aircraft.
    /// </summary>
    public static AircraftState Unknown(DateTimeOffset timestamp) => new()
    {
        Timestamp = timestamp,
        Position = GeoPosition.Create(0, 0),
        IndicatedAltitudeFeet = 0,
        PressureAltitudeFeet = 0,
        GroundSpeedKnots = 0,
        IndicatedAirspeedKnots = 0,
        TrueHeadingDegrees = 360,
        MagneticHeadingDegrees = 360,
        VerticalSpeedFeetPerMinute = 0,
        IsOnGround = true,
        ParkingBrakeSet = true,
        EnginesRunning = false,
        AircraftTitle = "Unknown",
        AtcIdentifier = string.Empty,
        AltitudeAboveGroundFeet = 0,
        AutopilotEngaged = false,
        GearHandleDown = true,
        FlapsHandleIndex = 0,
        Radio = RadioState.Default,
        Transponder = TransponderState.Default,
    };
}

/// <summary>COM radio tuning state.</summary>
public sealed record RadioState
{
    /// <summary>A neutral radio state with the conspicuity frequencies tuned.</summary>
    public static RadioState Default { get; } = new()
    {
        Com1Active = Frequency.FromMegahertz(121.500m),
        Com1Standby = Frequency.FromMegahertz(121.500m),
        Com2Active = Frequency.FromMegahertz(121.500m),
        Com2Standby = Frequency.FromMegahertz(121.500m),
        TransmitOnCom1 = true,
    };

    /// <summary>COM1 active frequency.</summary>
    public required Frequency Com1Active { get; init; }

    /// <summary>COM1 standby frequency.</summary>
    public required Frequency Com1Standby { get; init; }

    /// <summary>COM2 active frequency.</summary>
    public required Frequency Com2Active { get; init; }

    /// <summary>COM2 standby frequency.</summary>
    public required Frequency Com2Standby { get; init; }

    /// <summary>True when COM1 is the selected transmitter.</summary>
    public required bool TransmitOnCom1 { get; init; }

    /// <summary>The frequency the pilot would actually transmit on.</summary>
    public Frequency ActiveTransmitFrequency => TransmitOnCom1 ? Com1Active : Com2Active;
}

/// <summary>Transponder code and mode.</summary>
public sealed record TransponderState
{
    /// <summary>A neutral transponder state squawking the conspicuity code, standby.</summary>
    public static TransponderState Default { get; } = new()
    {
        Code = SquawkCode.Conspicuity,
        Mode = TransponderMode.Standby,
    };

    /// <summary>The code currently selected.</summary>
    public required SquawkCode Code { get; init; }

    /// <summary>The transponder mode, where the simulator reports it.</summary>
    public required TransponderMode Mode { get; init; }
}

/// <summary>Transponder operating mode.</summary>
public enum TransponderMode
{
    Off = 0,
    Standby,
    Test,

    /// <summary>Mode A/C — replying, no altitude.</summary>
    On,

    /// <summary>Mode C — replying with pressure altitude.</summary>
    AltitudeReporting,
}
