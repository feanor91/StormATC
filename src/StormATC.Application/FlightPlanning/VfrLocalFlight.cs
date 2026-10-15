using System.Diagnostics.CodeAnalysis;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.FlightPlanning;

/// <summary>
/// A local VFR flight: no flight plan, only the aerodrome the aircraft is on and its callsign.
/// </summary>
public static class VfrLocalFlight
{
    /// <summary>
    /// Builds the minimal plan of a local flight, or explains why the input cannot make one.
    /// </summary>
    /// <param name="aerodrome">The ICAO code, such as LFPL.</param>
    /// <param name="callsign">The callsign or registration, such as F-GABC.</param>
    /// <param name="now">The time the plan is generated.</param>
    /// <param name="plan">The plan, when the input is valid.</param>
    /// <param name="problem">Which input is wrong: <c>aerodrome</c> or <c>callsign</c>.</param>
    public static bool TryCreatePlan(
        string? aerodrome,
        string? callsign,
        DateTimeOffset now,
        [NotNullWhen(true)] out FlightPlan? plan,
        out string? problem)
    {
        string icao = (aerodrome ?? string.Empty).Trim().ToUpperInvariant();
        if (icao.Length != 4 || !icao.All(char.IsLetterOrDigit))
        {
            plan = null;
            problem = "aerodrome";
            return false;
        }

        if (!Callsign.TryCreate(callsign, out Callsign? parsed))
        {
            plan = null;
            problem = "callsign";
            return false;
        }

        plan = new FlightPlan
        {
            FlightPlanId = $"vfr-local-{icao}",
            Callsign = parsed,
            DepartureIcao = icao,
            DestinationIcao = icao,
            GeneratedAt = now,
            SourceIdentifier = "VFR local",
        };
        problem = null;
        return true;
    }
}
