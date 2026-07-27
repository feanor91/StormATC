using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Simulator;

/// <summary>
/// A scripted simulator that always works: no MSFS, no SDK, no network.
/// </summary>
/// <remarks>
/// <para>
/// The mock moves the aircraft; it does not decide anything about ATC. Phase advancement here
/// changes only physical state — position, altitude, speed, on-ground — and the real ATC engine
/// then observes that state and draws its own conclusions. That separation is what makes the
/// demonstration a genuine test of the architecture rather than a scripted playback: the mock
/// can put the aircraft on the runway, but only the engine can decide whether that was allowed.
/// </para>
/// <para>
/// Movement between waypoints is interpolated, so the aircraft is continuously somewhere
/// plausible rather than teleporting, which matters for the hold-short incursion check.
/// </para>
/// </remarks>
public sealed class MockSimulatorConnector(
    ILogger<MockSimulatorConnector> logger,
    IClock clock) : ISimulatorConnector
{
    private readonly ILogger<MockSimulatorConnector> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly Lock _stateLock = new();

    private CancellationTokenSource? _updateLoopCancellation;
    private Task? _updateLoop;
    private MockAircraftModel _model = MockAircraftModel.AtStand();
    private bool _disposed;

    /// <inheritdoc />
    public string ConnectorName => "Mock Simulator";

    /// <inheritdoc />
    public SimulatorConnectionState ConnectionState { get; private set; } =
        SimulatorConnectionState.Disconnected;

    /// <inheritdoc />
    public event EventHandler<AircraftStateChangedEventArgs>? AircraftStateChanged;

    /// <inheritdoc />
    public event EventHandler<SimulatorConnectionChangedEventArgs>? ConnectionChanged;

    /// <summary>The scripted phase the mock aircraft is currently in.</summary>
    public MockFlightStage Stage
    {
        get
        {
            lock (_stateLock)
            {
                return _model.Stage;
            }
        }
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (ConnectionState == SimulatorConnectionState.Connected)
        {
            return Task.CompletedTask;
        }

        SetConnectionState(SimulatorConnectionState.Connecting, "Starting the mock simulator.");

        // Not linked to the caller's token: that token scopes the connect operation, whereas the
        // update loop lives until DisconnectAsync. Linking them would end the loop as soon as
        // whatever kicked off the connection finished.
        _updateLoopCancellation = new CancellationTokenSource();
        _updateLoop = RunUpdateLoopAsync(_updateLoopCancellation.Token);

        SetConnectionState(
            SimulatorConnectionState.Connected,
            "Mock simulator running. No flight simulator is required.");

        _logger.LogInformation("Mock simulator connected.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_updateLoopCancellation is not null)
        {
            await _updateLoopCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_updateLoop is not null)
        {
            try
            {
                await _updateLoop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Expected during shutdown; the loop is cooperative and will exit.
            }
        }

        _updateLoopCancellation?.Dispose();
        _updateLoopCancellation = null;
        _updateLoop = null;

        SetConnectionState(SimulatorConnectionState.Disconnected, "Mock simulator stopped.");
    }

    /// <summary>Returns the current aircraft state without waiting for the next update.</summary>
    public AircraftState GetCurrentState()
    {
        lock (_stateLock)
        {
            return _model.ToAircraftState(_clock.UtcNow);
        }
    }

    // =====================================================================================
    // Developer controls. These change physical state only — never ATC state.
    // =====================================================================================

    /// <summary>Advances the mock aircraft to the next scripted stage.</summary>
    public void AdvanceStage()
    {
        lock (_stateLock)
        {
            MockFlightStage next = _model.Stage switch
            {
                MockFlightStage.AtStand => MockFlightStage.PushedBack,
                MockFlightStage.PushedBack => MockFlightStage.Taxiing,
                MockFlightStage.Taxiing => MockFlightStage.HoldingShort,
                MockFlightStage.HoldingShort => MockFlightStage.LiningUp,
                MockFlightStage.LiningUp => MockFlightStage.TakeoffRoll,
                MockFlightStage.TakeoffRoll => MockFlightStage.Climbing,
                MockFlightStage.Climbing => MockFlightStage.Cruise,
                MockFlightStage.Cruise => MockFlightStage.Descending,
                MockFlightStage.Descending => MockFlightStage.OnApproach,
                MockFlightStage.OnApproach => MockFlightStage.ShortFinal,
                MockFlightStage.ShortFinal => MockFlightStage.Landed,
                MockFlightStage.Landed => MockFlightStage.TaxiingIn,
                MockFlightStage.TaxiingIn => MockFlightStage.Parked,
                _ => MockFlightStage.Parked,
            };

            _model = _model.TransitionTo(next);
        }

        PublishState();
        _logger.LogInformation("Mock simulator advanced to {Stage}.", Stage);
    }

    /// <summary>Jumps directly to a stage.</summary>
    public void SetStage(MockFlightStage stage)
    {
        lock (_stateLock)
        {
            _model = _model.TransitionTo(stage);
        }

        PublishState();
        _logger.LogInformation("Mock simulator set to {Stage}.", stage);
    }

    /// <summary>Overrides the aircraft altitude.</summary>
    public void SetAltitude(double feet)
    {
        lock (_stateLock)
        {
            _model = _model with { AltitudeFeet = feet, IsOnGround = feet < 50 };
        }

        PublishState();
    }

    /// <summary>Overrides the aircraft heading.</summary>
    public void SetHeading(double degrees)
    {
        lock (_stateLock)
        {
            _model = _model with { HeadingDegrees = degrees };
        }

        PublishState();
    }

    /// <summary>Overrides the on-ground flag.</summary>
    public void SetOnGround(bool onGround)
    {
        lock (_stateLock)
        {
            _model = _model with { IsOnGround = onGround };
        }

        PublishState();
    }

    /// <summary>Overrides the aircraft position.</summary>
    public void SetPosition(GeoPosition position)
    {
        lock (_stateLock)
        {
            _model = _model with { Position = position };
        }

        PublishState();
    }

    /// <summary>Overrides the ground speed.</summary>
    public void SetGroundSpeed(double knots)
    {
        lock (_stateLock)
        {
            _model = _model with { GroundSpeedKnots = knots };
        }

        PublishState();
    }

    /// <summary>Sets the parking brake and engine state, used to complete the session.</summary>
    public void SetParked(bool parkingBrakeSet, bool enginesRunning)
    {
        lock (_stateLock)
        {
            _model = _model with { ParkingBrakeSet = parkingBrakeSet, EnginesRunning = enginesRunning };
        }

        PublishState();
    }

    /// <summary>Tunes the active COM1 frequency, mimicking the pilot changing radio.</summary>
    public void SetCom1Active(Frequency frequency)
    {
        lock (_stateLock)
        {
            _model = _model with { Com1Active = frequency };
        }

        PublishState();
    }

    /// <summary>
    /// Moves the aircraft onto the runway surface without a clearance, to exercise the
    /// hold-short violation path.
    /// </summary>
    public void TriggerRunwayIncursion(GeoPosition runwayPosition)
    {
        lock (_stateLock)
        {
            _model = _model with
            {
                Position = runwayPosition,
                IsOnGround = true,
                GroundSpeedKnots = 12,
                Stage = MockFlightStage.Taxiing,
            };
        }

        PublishState();
        _logger.LogWarning("Mock simulator moved the aircraft onto the runway without clearance.");
    }

    /// <summary>Puts the aircraft into a go-around: airborne, climbing, over the runway.</summary>
    public void TriggerGoAround()
    {
        lock (_stateLock)
        {
            _model = _model with
            {
                Stage = MockFlightStage.Climbing,
                IsOnGround = false,
                AltitudeFeet = Math.Max(_model.AltitudeFeet, 800),
                VerticalSpeedFeetPerMinute = 1800,
                GroundSpeedKnots = 160,
            };
        }

        PublishState();
    }

    /// <summary>Resets the mock aircraft to the stand at STMR.</summary>
    public void Reset()
    {
        lock (_stateLock)
        {
            _model = MockAircraftModel.AtStand();
        }

        PublishState();
    }

    // =====================================================================================

    private async Task RunUpdateLoopAsync(CancellationToken cancellationToken)
    {
        // Five updates per second, matching the SimConnect connector's target rate so both
        // paths exercise the engine identically.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_stateLock)
                {
                    _model = _model.Step(TimeSpan.FromMilliseconds(200));
                }

                PublishState();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The mock simulator update loop failed.");
            SetConnectionState(SimulatorConnectionState.Error, "The mock simulator stopped unexpectedly.");
        }
    }

    private void PublishState()
    {
        AircraftState state;
        lock (_stateLock)
        {
            state = _model.ToAircraftState(_clock.UtcNow);
        }

        AircraftStateChanged?.Invoke(this, new AircraftStateChangedEventArgs(state));
    }

    private void SetConnectionState(SimulatorConnectionState state, string? message)
    {
        ConnectionState = state;
        ConnectionChanged?.Invoke(this, new SimulatorConnectionChangedEventArgs(state, message));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>The scripted stages the mock aircraft moves through.</summary>
public enum MockFlightStage
{
    AtStand = 0,
    PushedBack,
    Taxiing,
    HoldingShort,
    LiningUp,
    TakeoffRoll,
    Climbing,
    Cruise,
    Descending,
    OnApproach,
    ShortFinal,
    Landed,
    TaxiingIn,
    Parked,
}

/// <summary>
/// The mock aircraft's physical state and the simple flight model that advances it.
/// </summary>
/// <remarks>
/// Coordinates come from the fictional STMR and STMD sample aerodromes. The model is
/// intentionally crude — constant rates per stage, linear interpolation — because its only job
/// is to produce state changes the ATC engine can react to.
/// </remarks>
internal sealed record MockAircraftModel
{
    // Key positions at the fictional aerodromes. These mirror data/sample-airports/*.json.
    private static readonly GeoPosition StandA1 = GeoPosition.Create(52.30550, 4.69400);
    private static readonly GeoPosition PushbackPoint = GeoPosition.Create(52.30700, 4.69550);
    private static readonly GeoPosition HoldingPoint18 = GeoPosition.Create(52.31050, 4.69940);
    private static readonly GeoPosition Runway18Threshold = GeoPosition.Create(52.31200, 4.70000);
    private static readonly GeoPosition Runway36DestThreshold = GeoPosition.Create(53.08800, 5.40000);
    private static readonly GeoPosition DestinationExit = GeoPosition.Create(53.10050, 5.40000);
    private static readonly GeoPosition DestinationStandC1 = GeoPosition.Create(53.10100, 5.39300);

    public required MockFlightStage Stage { get; init; }
    public required GeoPosition Position { get; init; }
    public required GeoPosition? TargetPosition { get; init; }
    public required double AltitudeFeet { get; init; }
    public required double TargetAltitudeFeet { get; init; }
    public required double GroundSpeedKnots { get; init; }
    public required double HeadingDegrees { get; init; }
    public required double VerticalSpeedFeetPerMinute { get; init; }
    public required bool IsOnGround { get; init; }
    public required bool ParkingBrakeSet { get; init; }
    public required bool EnginesRunning { get; init; }
    public Frequency Com1Active { get; init; } = Frequency.FromMegahertz(121.800m);
    public SquawkCode Squawk { get; init; } = SquawkCode.Conspicuity;

    /// <summary>Creates the initial state: parked at stand A1, STMR, cold.</summary>
    public static MockAircraftModel AtStand() => new()
    {
        Stage = MockFlightStage.AtStand,
        Position = StandA1,
        TargetPosition = null,
        AltitudeFeet = 20,
        TargetAltitudeFeet = 20,
        GroundSpeedKnots = 0,
        HeadingDegrees = 180,
        VerticalSpeedFeetPerMinute = 0,
        IsOnGround = true,
        ParkingBrakeSet = true,
        EnginesRunning = false,
    };

    /// <summary>Applies the physical setup for a stage.</summary>
    public MockAircraftModel TransitionTo(MockFlightStage stage) => stage switch
    {
        MockFlightStage.AtStand => AtStand(),

        MockFlightStage.PushedBack => this with
        {
            Stage = stage,
            Position = PushbackPoint,
            TargetPosition = null,
            GroundSpeedKnots = 0,
            HeadingDegrees = 0,
            IsOnGround = true,
            ParkingBrakeSet = true,
            EnginesRunning = true,
            AltitudeFeet = 20,
        },

        MockFlightStage.Taxiing => this with
        {
            Stage = stage,
            TargetPosition = HoldingPoint18,
            GroundSpeedKnots = 15,
            IsOnGround = true,
            ParkingBrakeSet = false,
            EnginesRunning = true,
            AltitudeFeet = 20,
        },

        MockFlightStage.HoldingShort => this with
        {
            Stage = stage,
            Position = HoldingPoint18,
            TargetPosition = null,
            GroundSpeedKnots = 0,
            HeadingDegrees = 90,
            IsOnGround = true,
            EnginesRunning = true,
            AltitudeFeet = 20,
        },

        MockFlightStage.LiningUp => this with
        {
            Stage = stage,
            Position = Runway18Threshold,
            TargetPosition = null,
            GroundSpeedKnots = 5,
            HeadingDegrees = 180,
            IsOnGround = true,
            EnginesRunning = true,
            AltitudeFeet = 20,
        },

        MockFlightStage.TakeoffRoll => this with
        {
            Stage = stage,
            GroundSpeedKnots = 80,
            HeadingDegrees = 180,
            IsOnGround = true,
            EnginesRunning = true,
            TargetAltitudeFeet = 7000,
        },

        // Airborne, climbing towards the initial altitude, tracking the destination.
        MockFlightStage.Climbing => this with
        {
            Stage = stage,
            IsOnGround = false,
            GroundSpeedKnots = 220,
            AltitudeFeet = Math.Max(AltitudeFeet, 1200),
            TargetAltitudeFeet = 7000,
            VerticalSpeedFeetPerMinute = 2200,
            TargetPosition = GeoPosition.Create(53.10000, 5.40000),
        },

        MockFlightStage.Cruise => this with
        {
            Stage = stage,
            IsOnGround = false,
            GroundSpeedKnots = 420,
            AltitudeFeet = 12000,
            TargetAltitudeFeet = 12000,
            VerticalSpeedFeetPerMinute = 0,
            TargetPosition = GeoPosition.Create(53.10000, 5.40000),
        },

        MockFlightStage.Descending => this with
        {
            Stage = stage,
            IsOnGround = false,
            GroundSpeedKnots = 300,
            TargetAltitudeFeet = 7000,
            VerticalSpeedFeetPerMinute = -1800,
        },

        // Positioned on final for runway 36 at the destination.
        MockFlightStage.OnApproach => this with
        {
            Stage = stage,
            IsOnGround = false,
            Position = GeoPosition.Create(53.05000, 5.40000),
            TargetPosition = Runway36DestThreshold,
            AltitudeFeet = 3000,
            TargetAltitudeFeet = 3000,
            GroundSpeedKnots = 180,
            HeadingDegrees = 360,
            VerticalSpeedFeetPerMinute = 0,
        },

        MockFlightStage.ShortFinal => this with
        {
            Stage = stage,
            IsOnGround = false,
            Position = GeoPosition.Create(53.08000, 5.40000),
            TargetPosition = Runway36DestThreshold,
            AltitudeFeet = 600,
            TargetAltitudeFeet = 45,
            GroundSpeedKnots = 140,
            HeadingDegrees = 360,
            VerticalSpeedFeetPerMinute = -700,
        },

        MockFlightStage.Landed => this with
        {
            Stage = stage,
            IsOnGround = true,
            Position = DestinationExit,
            TargetPosition = null,
            AltitudeFeet = 45,
            TargetAltitudeFeet = 45,
            GroundSpeedKnots = 25,
            HeadingDegrees = 360,
            VerticalSpeedFeetPerMinute = 0,
            EnginesRunning = true,
        },

        MockFlightStage.TaxiingIn => this with
        {
            Stage = stage,
            IsOnGround = true,
            TargetPosition = DestinationStandC1,
            GroundSpeedKnots = 15,
            AltitudeFeet = 45,
            ParkingBrakeSet = false,
            EnginesRunning = true,
        },

        MockFlightStage.Parked => this with
        {
            Stage = stage,
            IsOnGround = true,
            Position = DestinationStandC1,
            TargetPosition = null,
            GroundSpeedKnots = 0,
            AltitudeFeet = 45,
            VerticalSpeedFeetPerMinute = 0,
            ParkingBrakeSet = true,
            EnginesRunning = false,
        },

        _ => this with { Stage = stage },
    };

    /// <summary>Advances the model by one tick.</summary>
    public MockAircraftModel Step(TimeSpan elapsed)
    {
        double hours = elapsed.TotalHours;
        double minutes = elapsed.TotalMinutes;

        double altitude = AltitudeFeet;
        double verticalSpeed = VerticalSpeedFeetPerMinute;

        // Vertical: move towards the target and stop cleanly rather than oscillating.
        if (Math.Abs(TargetAltitudeFeet - altitude) > 10 && Math.Abs(verticalSpeed) > 1)
        {
            double delta = verticalSpeed * minutes;
            double next = altitude + delta;

            bool overshotClimb = verticalSpeed > 0 && next >= TargetAltitudeFeet;
            bool overshotDescent = verticalSpeed < 0 && next <= TargetAltitudeFeet;

            if (overshotClimb || overshotDescent)
            {
                altitude = TargetAltitudeFeet;
                verticalSpeed = 0;
            }
            else
            {
                altitude = next;
            }
        }

        // Lateral: track towards the target position at the current ground speed.
        GeoPosition position = Position;
        double heading = HeadingDegrees;
        GeoPosition? target = TargetPosition;

        if (target is GeoPosition destination && GroundSpeedKnots > 0.1)
        {
            double distanceToTarget = position.DistanceNauticalMilesTo(destination);
            double travelled = GroundSpeedKnots * hours;

            if (travelled >= distanceToTarget)
            {
                position = destination;
                target = null;
            }
            else
            {
                Heading bearing = position.TrueBearingTo(destination);
                position = position.Project(bearing, travelled);
                heading = bearing.Degrees;
            }
        }

        // Takeoff roll: accelerate, then rotate at a plausible speed.
        if (Stage == MockFlightStage.TakeoffRoll)
        {
            double speed = Math.Min(GroundSpeedKnots + (60 * minutes * 60), 170);

            if (speed >= 150)
            {
                return TransitionTo(MockFlightStage.Climbing) with { GroundSpeedKnots = speed };
            }

            return this with
            {
                GroundSpeedKnots = speed,
                Position = position.Project(Heading.FromDegrees(heading), speed * hours),
            };
        }

        // Short final: touch down once the aircraft reaches the runway elevation.
        if (Stage == MockFlightStage.ShortFinal && altitude <= TargetAltitudeFeet + 5)
        {
            return TransitionTo(MockFlightStage.Landed);
        }

        return this with
        {
            Position = position,
            TargetPosition = target,
            AltitudeFeet = altitude,
            VerticalSpeedFeetPerMinute = verticalSpeed,
            HeadingDegrees = heading,
            IsOnGround = IsOnGround && altitude < 100,
        };
    }

    /// <summary>Projects the model into the domain's aircraft snapshot.</summary>
    public AircraftState ToAircraftState(DateTimeOffset timestamp) => new()
    {
        Timestamp = timestamp,
        Position = Position,
        IndicatedAltitudeFeet = AltitudeFeet,

        // Standard-datum pressure altitude differs from indicated by the QNH offset. The mock
        // uses 1013 everywhere, so the two are equal.
        PressureAltitudeFeet = AltitudeFeet,
        GroundSpeedKnots = GroundSpeedKnots,
        IndicatedAirspeedKnots = IsOnGround ? GroundSpeedKnots : GroundSpeedKnots * 0.85,
        TrueHeadingDegrees = HeadingDegrees,
        MagneticHeadingDegrees = HeadingDegrees,
        VerticalSpeedFeetPerMinute = VerticalSpeedFeetPerMinute,
        IsOnGround = IsOnGround,
        ParkingBrakeSet = ParkingBrakeSet,
        EnginesRunning = EnginesRunning,
        AircraftTitle = "StormATC Mock A320",
        Radio = new RadioState
        {
            Com1Active = Com1Active,
            Com1Standby = Frequency.FromMegahertz(121.500m),
            Com2Active = Frequency.FromMegahertz(121.500m),
            Com2Standby = Frequency.FromMegahertz(121.500m),
            TransmitOnCom1 = true,
        },
        Transponder = new TransponderState
        {
            Code = Squawk,
            Mode = IsOnGround ? TransponderMode.Standby : TransponderMode.AltitudeReporting,
        },
    };
}
