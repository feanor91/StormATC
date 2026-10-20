#if MSFS_SDK_AVAILABLE
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
#endif
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.FlightPlanning;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;

namespace StormATC.Infrastructure.Simulator;

/// <summary>The part of the connector that reads instrument approaches from the simulator's navigation data.</summary>
public sealed partial class MsfsSimConnectConnector : IApproachProcedureSource
{
    /// <summary>One approach of an aerodrome, with the legs of its missed approach.</summary>
    internal sealed record ApproachFacility(int Type, int RunwayNumber, int RunwayDesignator, char Suffix, List<MissedLeg> MissedLegs);

    /// <summary>One leg of a missed approach, as the navigation data gives it.</summary>
    internal sealed record MissedLeg(int LegType, string Fix, int? AltitudeFeet);

#if MSFS_SDK_AVAILABLE
    private readonly System.Collections.Concurrent.ConcurrentQueue<ApproachRequest> _approachRequestQueue = new();
    private readonly Dictionary<string, List<ApproachFacility>> _approachCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _approachCacheLock = new();
    private ApproachRequest? _activeApproachRequest;

    private sealed record ApproachRequest(
        string Icao,
        TaskCompletionSource<List<ApproachFacility>?> Completion,
        List<ApproachFacility> Approaches);

    /// <summary>The fields of an approach, in the order the definition asks for them.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ApproachFacilityStruct
    {
        public int Type;
        public int RunwayNumber;
        public int RunwayDesignator;
        public byte Suffix;
    }

    /// <summary>The fields of a missed approach leg, in the order the definition asks for them.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct MissedLegFacilityStruct
    {
        public int Type;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
        public string FixIcao;

        // Metres; zero when the leg has no altitude.
        public float Altitude1Meters;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MissedApproachPoint>?> GetMissedApproachAsync(
        string airportIcao,
        SimApproach approach,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approach);
        if (ConnectionState != SimulatorConnectionState.Connected || string.IsNullOrWhiteSpace(airportIcao))
        {
            return null;
        }

        List<ApproachFacility>? approaches;
        lock (_approachCacheLock)
        {
            _approachCache.TryGetValue(airportIcao, out approaches);
        }

        if (approaches is null)
        {
            var completion = new TaskCompletionSource<List<ApproachFacility>?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _approachRequestQueue.Enqueue(new ApproachRequest(airportIcao.Trim().ToUpperInvariant(), completion, []));
            _airportRequestSignal.Set();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                approaches = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                completion.TrySetResult(null);
                _logger.LogWarning("MSFS did not answer the approach data request for {Icao} in time.", airportIcao);
                return null;
            }

            if (approaches is null)
            {
                return null;
            }

            lock (_approachCacheLock)
            {
                _approachCache[airportIcao] = approaches;
            }
        }

        return MissedApproachMapper.Find(approaches, approach, _logger);
    }

    private static void RegisterApproachFacilityDefinition(SimConnect simConnect)
    {
        // The aerodrome item is read with the same fields as in the airport definition: the simulator
        // gives one structure per kind of item.
        simConnect.AddToFacilityDefinition(Definitions.ApproachFacility, "OPEN AIRPORT");
        foreach (string field in new[] { "LATITUDE", "LONGITUDE", "ALTITUDE", "MAGVAR", "NAME64", "ICAO" })
        {
            simConnect.AddToFacilityDefinition(Definitions.ApproachFacility, field);
        }

        simConnect.AddToFacilityDefinition(Definitions.ApproachFacility, "OPEN APPROACH");
        foreach (string field in new[] { "TYPE", "RUNWAY_NUMBER", "RUNWAY_DESIGNATOR", "SUFFIX" })
        {
            simConnect.AddToFacilityDefinition(Definitions.ApproachFacility, field);
        }

        simConnect.AddToFacilityDefinition(Definitions.ApproachFacility, "OPEN MISSED_APPROACH_LEG");
        foreach (string field in new[] { "TYPE", "FIX_ICAO", "ALTITUDE1" })
        {
            simConnect.AddToFacilityDefinition(Definitions.ApproachFacility, field);
        }

        simConnect.AddToFacilityDefinition(Definitions.ApproachFacility, "CLOSE MISSED_APPROACH_LEG");
        simConnect.AddToFacilityDefinition(Definitions.ApproachFacility, "CLOSE APPROACH");
        simConnect.AddToFacilityDefinition(Definitions.ApproachFacility, "CLOSE AIRPORT");

        simConnect.RegisterFacilityDataDefineStruct<ApproachFacilityStruct>(SIMCONNECT_FACILITY_DATA_TYPE.APPROACH);
        simConnect.RegisterFacilityDataDefineStruct<MissedLegFacilityStruct>(SIMCONNECT_FACILITY_DATA_TYPE.MISSED_APPROACH_LEG);
    }

    /// <summary>Starts the next approach request when the facility channel is free. True when it started one.</summary>
    private bool StartNextApproachRequest()
    {
        if (_activeApproachRequest is not null || _simConnect is null)
        {
            return _activeApproachRequest is not null;
        }

        while (_approachRequestQueue.TryDequeue(out ApproachRequest? request))
        {
            if (request.Completion.Task.IsCompleted)
            {
                continue;
            }

            _activeApproachRequest = request;
            try
            {
                _simConnect.RequestFacilityData(
                    Definitions.ApproachFacility,
                    Requests.ApproachFacility,
                    request.Icao,
                    string.Empty);
                _logger.LogInformation("Requested the approaches of {Icao} from the simulator.", request.Icao);
            }
            catch (Exception ex)
            {
                _activeApproachRequest = null;
                request.Completion.TrySetResult(null);
                _logger.LogWarning(ex, "Requesting the approaches of {Icao} failed.", request.Icao);
                continue;
            }

            return true;
        }

        return false;
    }

    private void OnReceiveApproachData(SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (_activeApproachRequest is not ApproachRequest request || data.Data.Length == 0)
        {
            return;
        }

        switch ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type)
        {
            case SIMCONNECT_FACILITY_DATA_TYPE.APPROACH when data.Data[0] is ApproachFacilityStruct approach:
                request.Approaches.Add(new ApproachFacility(
                    approach.Type,
                    approach.RunwayNumber,
                    approach.RunwayDesignator,
                    approach.Suffix is >= 0x20 and < 0x7F ? (char)approach.Suffix : ' ',
                    []));
                break;

            case SIMCONNECT_FACILITY_DATA_TYPE.MISSED_APPROACH_LEG when data.Data[0] is MissedLegFacilityStruct leg
                && request.Approaches.Count > 0:
                int? feet = leg.Altitude1Meters > 0 ? (int)Math.Round(leg.Altitude1Meters * 3.28084) : null;
                request.Approaches[^1].MissedLegs.Add(new MissedLeg(leg.Type, (leg.FixIcao ?? string.Empty).Trim(), feet));
                break;
        }
    }

    private void OnReceiveApproachDataEnd()
    {
        if (_activeApproachRequest is not ApproachRequest request)
        {
            return;
        }

        _activeApproachRequest = null;
        _logger.LogInformation(
            "The simulator gave {Count} approaches for {Icao}.",
            request.Approaches.Count,
            request.Icao);
        request.Completion.TrySetResult(request.Approaches);
        _airportRequestSignal.Set();
    }

    private void CancelApproachRequests()
    {
        _activeApproachRequest?.Completion.TrySetResult(null);
        _activeApproachRequest = null;
    }
#else
    /// <inheritdoc />
    public Task<IReadOnlyList<MissedApproachPoint>?> GetMissedApproachAsync(
        string airportIcao,
        SimApproach approach,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MissedApproachPoint>?>(null);
#endif
}

/// <summary>Picks an approach in an aerodrome's navigation data and names the points of its missed approach.</summary>
internal static class MissedApproachMapper
{
    /// <summary>The missed approach of the approach the plan names, or null when none matches.</summary>
    public static IReadOnlyList<MissedApproachPoint>? Find(
        IReadOnlyList<MsfsSimConnectConnector.ApproachFacility> approaches,
        SimApproach wanted,
        ILogger logger)
    {
        int type = TypeCode(wanted.Type);
        (int number, int designator) = Runway(wanted.Runway);
        char suffix = wanted.Suffix.Length > 0 ? wanted.Suffix[0] : ' ';

        MsfsSimConnectConnector.ApproachFacility? match = approaches.FirstOrDefault(a =>
            a.Type == type && a.RunwayNumber == number && a.RunwayDesignator == designator && a.Suffix == suffix)
            ?? approaches.FirstOrDefault(a =>
                a.Type == type && a.RunwayNumber == number && a.RunwayDesignator == designator);

        if (match is null)
        {
            logger.LogInformation(
                "None of the {Count} approaches the simulator gave is {Approach}.",
                approaches.Count,
                wanted);
            return null;
        }

        return Points(match.MissedLegs);
    }

    /// <summary>The points of a missed approach, in order.</summary>
    public static IReadOnlyList<MissedApproachPoint> Points(IEnumerable<MsfsSimConnectConnector.MissedLeg> legs)
    {
        List<MissedApproachPoint> points = [];
        foreach (MsfsSimConnectConnector.MissedLeg leg in legs)
        {
            string name = leg.LegType switch
            {
                5 or 21 => "INTRCPT",
                12 or 13 or 14 => leg.Fix.Length > 0 ? "HOLD " + leg.Fix : "HOLD",
                _ => leg.Fix,
            };

            if (name.Length > 0)
            {
                points.Add(new MissedApproachPoint(name, leg.AltitudeFeet));
            }
        }

        return points;
    }

    /// <summary>The simulator's number for an approach type, as the plan names it.</summary>
    internal static int TypeCode(string type) =>
        type.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
        {
            "GPS" => 1,
            "VOR" => 2,
            "NDB" => 3,
            "ILS" => 4,
            "LOC" or "LOCALIZER" => 5,
            "SDF" => 6,
            "LDA" => 7,
            "VORDME" => 8,
            "NDBDME" => 9,
            "RNAV" => 10,
            "LOCBC" or "LOCALIZERBACKCOURSE" => 11,
            _ => -1,
        };

    /// <summary>The runway number and designator code (0 none, 1 left, 2 right, 3 center) of a runway such as <c>04R</c>.</summary>
    internal static (int Number, int Designator) Runway(string runway)
    {
        string digits = new(runway.TakeWhile(char.IsDigit).ToArray());
        int number = int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        int designator = runway.Length > digits.Length
            ? char.ToUpperInvariant(runway[digits.Length]) switch { 'L' => 1, 'R' => 2, 'C' => 3, _ => 0 }
            : 0;
        return (number, designator);
    }
}
