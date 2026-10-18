using StormATC.Application.FlightPlanning;
using StormATC.Domain.FlightPlanning;

namespace StormATC.Application.Tests;

/// <summary>Reading the flight plan files that Microsoft Flight Simulator writes.</summary>
public sealed class SimFlightPlanTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "stormatc-pln-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // What the simulator wrote when a free flight was started at Lognes.
    private const string CustomFlight = """
        <?xml version="1.0" encoding="UTF-8"?>

        <SimBase.Document>
            <FlightPlan.FlightPlan>
                <DepartureID>LFPL</DepartureID>
                <DestinationID>LFPGO</DestinationID>
                <Title>LFPL - LFPGO</Title>
                <Descr>Flight from LFPL to LFPGO</Descr>
                <FPType>VFR</FPType>
                <CruisingAlt>45000.000</CruisingAlt>
                <DepartureDetails>
                    <RunwayNumberFP>8</RunwayNumberFP>
                </DepartureDetails>
            </FlightPlan.FlightPlan>
        </SimBase.Document>
        """;

    private const string CrossCountry = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SimBase.Document Type="AceXML" version="1,0">
            <FlightPlan.FlightPlan>
                <Title>Lognes to Pontoise</Title>
                <FPType>VFR</FPType>
                <CruisingAlt>2500.000</CruisingAlt>
                <DepartureID>LFPL</DepartureID>
                <DestinationID>LFPT</DestinationID>
                <DepartureDetails>
                    <RunwayNumberFP>26</RunwayNumberFP>
                    <RunwayDesignatorFP>LEFT</RunwayDesignatorFP>
                </DepartureDetails>
                <ATCWaypoint id="LFPL">
                    <ATCWaypointType>Airport</ATCWaypointType>
                    <WorldPosition>N48°49'31.80",E2°37'30.00",+000290.00</WorldPosition>
                </ATCWaypoint>
                <ATCWaypoint id="MELUN">
                    <ATCWaypointType>User</ATCWaypointType>
                    <WorldPosition>N48°32'00.00",E2°40'00.00",+002500.00</WorldPosition>
                </ATCWaypoint>
                <ATCWaypoint id="SOUTH">
                    <ATCWaypointType>User</ATCWaypointType>
                    <WorldPosition>S10°30'00.00",W20°15'00.00",+002500.00</WorldPosition>
                </ATCWaypoint>
                <ATCWaypoint id="LFPT">
                    <ATCWaypointType>Airport</ATCWaypointType>
                    <WorldPosition>N49°05'59.00",E2°02'25.00",+000325.00</WorldPosition>
                </ATCWaypoint>
            </FlightPlan.FlightPlan>
        </SimBase.Document>
        """;

    // A plan saved from the EFB: Lognes to Le Puy, with the arrival runway and visual patterns.
    private const string SavedFromTheEfb = """
        <?xml version="1.0" encoding="UTF-8"?>

        <SimBase.Document>
            <FlightPlan.FlightPlan>
                <DepartureID>LFPL</DepartureID>
                <DestinationID>LFHP</DestinationID>
                <Title>LFPL - LFHP</Title>
                <FPType>VFR</FPType>
                <CruisingAlt>45000.000</CruisingAlt>
                <DepartureDetails>
                    <RunwayNumberFP>8</RunwayNumberFP>
                    <DepartureVisualPattern>
                        <VisualPatternDistance>1.000</VisualPatternDistance>
                        <VisualPatternAltitude>1000.000</VisualPatternAltitude>
                    </DepartureVisualPattern>
                </DepartureDetails>
                <ArrivalDetails>
                    <RunwayNumberFP>33</RunwayNumberFP>
                </ArrivalDetails>
                <ApproachDetails>
                    <ApproachVisualPattern>
                        <ApproachVisualPatternType>Downwind</ApproachVisualPatternType>
                    </ApproachVisualPattern>
                </ApproachDetails>
            </FlightPlan.FlightPlan>
        </SimBase.Document>
        """;

    [Fact]
    public void A_plan_saved_from_the_EFB_gives_both_runways()
    {
        Assert.True(PlnFlightPlanParser.TryParse(SavedFromTheEfb, out SimFlightPlan? plan));

        Assert.Equal("LFPL", plan.DepartureIcao);
        Assert.Equal("LFHP", plan.DestinationIcao);
        Assert.Equal("08", plan.DepartureRunway);
        Assert.Equal("33", plan.ArrivalRunway);
        Assert.True(plan.IsVfr);
    }

    [Fact]
    public void The_default_altitude_of_a_vfr_plan_is_not_taken_for_a_cruising_altitude()
    {
        Assert.True(PlnFlightPlanParser.TryParse(SavedFromTheEfb, out SimFlightPlan? plan));
        Assert.True(SimFlightPlanConverter.TryConvert(plan, "F-HAST", Fixture.Now, out FlightPlan? flightPlan));

        // 45,000 ft is what the simulator writes when nobody set an altitude.
        Assert.Null(flightPlan.CruiseAltitude);
        Assert.Equal("33", flightPlan.ArrivalRunway!.Value.ToString());
    }

    [Fact]
    public void The_plan_of_a_started_free_flight_is_read()
    {
        Assert.True(PlnFlightPlanParser.TryParse(CustomFlight, out SimFlightPlan? plan));

        Assert.Equal("LFPL", plan.DepartureIcao);
        Assert.True(plan.IsVfr);
        Assert.Equal("08", plan.DepartureRunway);
        Assert.Empty(plan.Waypoints);
    }

    [Fact]
    public void A_destination_that_is_not_an_aerodrome_code_makes_it_a_local_flight()
    {
        Assert.True(PlnFlightPlanParser.TryParse(CustomFlight, out SimFlightPlan? plan));

        // "LFPGO" is five characters: the departure aerodrome is the destination too.
        Assert.Equal("LFPL", plan.DestinationIcao);
    }

    [Fact]
    public void A_cross_country_plan_gives_its_aerodromes_runway_and_waypoints()
    {
        Assert.True(PlnFlightPlanParser.TryParse(CrossCountry, out SimFlightPlan? plan));

        Assert.Equal("LFPL", plan.DepartureIcao);
        Assert.Equal("LFPT", plan.DestinationIcao);
        Assert.Equal("26L", plan.DepartureRunway);
        Assert.Equal(2500, plan.CruisingAltitudeFeet);
        Assert.Equal(["MELUN", "SOUTH"], plan.Waypoints.Select(w => w.Identifier));
    }

    [Fact]
    public void Positions_are_read_with_their_hemispheres()
    {
        Assert.True(PlnFlightPlanParser.TryParse(CrossCountry, out SimFlightPlan? plan));

        Assert.Equal(48.5333, plan.Waypoints[0].Position!.Value.Latitude, 3);
        Assert.Equal(2.6667, plan.Waypoints[0].Position!.Value.Longitude, 3);
        Assert.Equal(-10.5, plan.Waypoints[1].Position!.Value.Latitude, 3);
        Assert.Equal(-20.25, plan.Waypoints[1].Position!.Value.Longitude, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<SimBase.Document></SimBase.Document>")]
    [InlineData("<SimBase.Document><FlightPlan.FlightPlan><Title>x</Title></FlightPlan.FlightPlan></SimBase.Document>")]
    public void A_file_that_holds_no_plan_is_refused(string text) =>
        Assert.False(PlnFlightPlanParser.TryParse(text, out _));

    [Fact]
    public void A_plan_becomes_the_flight_plan_StormATC_works_from()
    {
        Assert.True(PlnFlightPlanParser.TryParse(CrossCountry, out SimFlightPlan? plan));

        Assert.True(SimFlightPlanConverter.TryConvert(plan, "F-HAST", Fixture.Now, out FlightPlan? flightPlan));

        Assert.Equal("LFPL", flightPlan.DepartureIcao);
        Assert.Equal("LFPT", flightPlan.DestinationIcao);
        Assert.Equal("FHAST", flightPlan.Callsign.Normalized);
        Assert.Equal("26L", flightPlan.DepartureRunway!.Value.ToString());
        Assert.Equal("MELUN SOUTH", flightPlan.Route);
        Assert.Equal(2, flightPlan.RouteLegs.Count);
        Assert.Equal(2500, flightPlan.CruiseAltitude!.Value.Feet);
        Assert.Equal("Simulator", flightPlan.SourceIdentifier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_plan_cannot_be_used_without_a_callsign(string? callsign)
    {
        Assert.True(PlnFlightPlanParser.TryParse(CrossCountry, out SimFlightPlan? plan));

        Assert.False(SimFlightPlanConverter.TryConvert(plan, callsign, Fixture.Now, out _));
    }

    private static FlightPlan Converted(string text = CrossCountry, string callsign = "F-HAST")
    {
        Assert.True(PlnFlightPlanParser.TryParse(text, out SimFlightPlan? plan));
        Assert.True(SimFlightPlanConverter.TryConvert(plan, callsign, Fixture.Now, out FlightPlan? flightPlan));
        return flightPlan;
    }

    [Fact]
    public void A_plan_announced_again_is_the_same_plan()
    {
        Assert.True(SimFlightPlanComparison.IsSamePlan(Converted(), Converted()));
    }

    [Fact]
    public void Another_destination_runway_route_or_aircraft_is_another_plan()
    {
        FlightPlan current = Converted();

        Assert.False(SimFlightPlanComparison.IsSamePlan(current, Converted(CustomFlight)));
        Assert.False(SimFlightPlanComparison.IsSamePlan(current, Converted(callsign: "F-GXYZ")));
        Assert.False(SimFlightPlanComparison.IsSamePlan(
            current,
            Converted(CrossCountry.Replace("<RunwayNumberFP>26</RunwayNumberFP>", "<RunwayNumberFP>8</RunwayNumberFP>"))));
        Assert.False(SimFlightPlanComparison.IsSamePlan(
            current,
            Converted(CrossCountry.Replace("<ATCWaypoint id=\"SOUTH\">", "<ATCWaypoint id=\"EAST\">"))));
    }

    [Fact]
    public void A_plan_from_another_source_or_none_is_never_the_same()
    {
        FlightPlan plan = Converted();

        Assert.False(SimFlightPlanComparison.IsSamePlan(null, plan));
        Assert.False(SimFlightPlanComparison.IsSamePlan(plan with { SourceIdentifier = "SimBrief" }, plan));
    }

    [Fact]
    public void The_newest_plan_file_is_found()
    {
        Directory.CreateDirectory(_folder);
        string old = Path.Combine(_folder, "OLD.PLN");
        string recent = Path.Combine(_folder, "CUSTOMFLIGHT.PLN");
        File.WriteAllText(old, CrossCountry);
        File.WriteAllText(recent, CustomFlight);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow);
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a plan");

        Assert.Equal(recent, SimFlightPlanLocator.FindNewest([Path.Combine(_folder, "missing"), _folder]));
    }

    [Fact]
    public void No_plan_file_means_none_is_found()
    {
        Directory.CreateDirectory(_folder);

        Assert.Null(SimFlightPlanLocator.FindNewest([_folder, Path.Combine(_folder, "missing")]));
    }

    // The plan saved from the EFB for an IFR flight, with its departure, arrival and approach.
    private const string OrlyNice = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SimBase.Document>
          <FlightPlan.FlightPlan>
            <DepartureID>LFPO</DepartureID>
            <DestinationID>LFMN</DestinationID>
            <FPType>IFR</FPType>
            <CruisingAlt>45000.000</CruisingAlt>
            <DepartureDetails><RunwayNumberFP>6</RunwayNumberFP><DepartureFP>PILU2F</DepartureFP></DepartureDetails>
            <ATCWaypoint id="FF307"><ATCWaypointType>Intersection</ATCWaypointType></ATCWaypoint>
            <ArrivalDetails><RunwayNumberFP>4</RunwayNumberFP><RunwayDesignatorFP>RIGHT</RunwayDesignatorFP><ArrivalFP>VEVA7R</ArrivalFP></ArrivalDetails>
            <ApproachDetails>
              <ApproachTypeFP>ILS</ApproachTypeFP><RunwayNumberFP>4</RunwayNumberFP><RunwayDesignatorFP>RIGHT</RunwayDesignatorFP>
              <SuffixFP>Y</SuffixFP><TransitionFP>NERA1</TransitionFP>
            </ApproachDetails>
          </FlightPlan.FlightPlan>
        </SimBase.Document>
        """;

    [Fact]
    public void An_ifr_plan_gives_its_procedures_to_the_flight_plan()
    {
        Assert.True(PlnFlightPlanParser.TryParse(OrlyNice, out SimFlightPlan? plan));
        Assert.Equal("PILU2F", plan.DepartureProcedure);
        Assert.Equal("VEVA7R", plan.ArrivalProcedure);
        Assert.Equal("ILS Y 04R", plan.Approach!.ToString());

        Assert.True(SimFlightPlanConverter.TryConvert(plan, "F-HAST", Fixture.Now, out FlightPlan? flightPlan));

        Assert.Equal("PILU2F", flightPlan.StandardInstrumentDeparture);
        Assert.Equal("VEVA7R", flightPlan.StandardTerminalArrival);
        Assert.Equal("ILS Y 04R", flightPlan.Approach);
        Assert.Equal(45000, flightPlan.CruiseAltitude!.Value.Feet);
    }

    [Fact]
    public void A_vfr_plan_keeps_no_instrument_procedure()
    {
        Assert.True(PlnFlightPlanParser.TryParse(OrlyNice.Replace("IFR", "VFR", StringComparison.Ordinal), out SimFlightPlan? plan));

        Assert.True(SimFlightPlanConverter.TryConvert(plan, "F-HAST", Fixture.Now, out FlightPlan? flightPlan));

        Assert.Null(flightPlan.StandardInstrumentDeparture);
        Assert.Null(flightPlan.StandardTerminalArrival);
        Assert.Null(flightPlan.Approach);
    }
}
