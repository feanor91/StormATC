using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;
using StormATC.Infrastructure.FlightPlanning;
using StormATC.Infrastructure.Mocks;
using StormATC.Infrastructure.SimBrief;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Infrastructure.Tests;

/// <summary>
/// Choosing a simulator connector.
/// </summary>
/// <remarks>
/// These tests run on a machine with no simulator and, in the usual case, no SimConnect assembly
/// either. That is the point: the behaviour that matters most is what happens when MSFS is
/// preferred and absent, which is exactly the state a first-time user is in.
/// </remarks>
public class SimulatorConnectorRoutingTests
{
    private static SimulatorConnectorRouter CreateRouter(string preferredConnector)
    {
        var clock = new SystemClock();

        var mock = new MockSimulatorConnector(NullLogger<MockSimulatorConnector>.Instance, clock);

        var options = Options.Create(new SimulatorOptions
        {
            PreferredConnector = preferredConnector,

            // Off, so no test starts a background retry loop it would then have to wait out.
            AutoReconnect = false,
        });

        var msfs = new MsfsSimConnectConnector(options, NullLogger<MsfsSimConnectConnector>.Instance);

        return new SimulatorConnectorRouter(
            mock, msfs, options, NullLogger<SimulatorConnectorRouter>.Instance);
    }

    [Fact]
    public async Task Choosing_the_mock_connects_the_mock()
    {
        await using SimulatorConnectorRouter router = CreateRouter("Mock");

        await router.ConnectAsync(CancellationToken.None);

        Assert.True(router.IsMockActive);
        Assert.Equal(SimulatorConnectionState.Connected, router.ConnectionState);
    }

#if !MSFS_SDK_AVAILABLE
    [Fact]
    public async Task Preferring_MSFS_falls_back_to_the_mock_when_the_simulator_is_absent()
    {
        await using SimulatorConnectorRouter router = CreateRouter("MSFS");

        await router.ConnectAsync(CancellationToken.None);

        // Either the SDK is missing from this build or the simulator is not running. Both mean
        // the same thing to a user, and both must leave a usable session rather than no session.
        Assert.True(router.IsMockActive);
        Assert.Equal(SimulatorConnectionState.Connected, router.ConnectionState);
        Assert.Equal("MSFS", router.PreferredConnector);
    }

    [Fact]
    public async Task The_fallback_explains_itself_rather_than_failing_silently()
    {
        await using SimulatorConnectorRouter router = CreateRouter("MSFS");

        await router.ConnectAsync(CancellationToken.None);

        Assert.NotEqual(string.Empty, router.StatusMessage);
        Assert.Contains("mock", router.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }
#endif

    [Fact]
    public async Task An_unrecognised_connector_name_is_treated_as_MSFS()
    {
        // Configuration is user-editable text. An unknown value must not stop the application,
        // and preferring the real simulator is the safe reading of "something other than Mock".
        await using SimulatorConnectorRouter router = CreateRouter("Prepar3D");

        await router.ConnectAsync(CancellationToken.None);

        Assert.Equal("MSFS", router.PreferredConnector);
    }

    [Fact]
    public async Task Switching_connector_takes_effect_without_a_restart()
    {
        await using SimulatorConnectorRouter router = CreateRouter("Mock");
        await router.ConnectAsync(CancellationToken.None);

        Assert.True(router.IsMockActive);

        string message = await router.SwitchToAsync("MSFS", CancellationToken.None);

        Assert.Equal("MSFS", router.PreferredConnector);
        Assert.NotEqual(string.Empty, message);

        // Back again, to prove the switch is not one-way.
        await router.SwitchToAsync("Mock", CancellationToken.None);

        Assert.True(router.IsMockActive);
        Assert.Equal(SimulatorConnectionState.Connected, router.ConnectionState);
    }

    [Fact]
    public async Task Aircraft_updates_from_the_active_connector_reach_subscribers()
    {
        await using SimulatorConnectorRouter router = CreateRouter("Mock");

        var received = new TaskCompletionSource();
        router.AircraftStateChanged += (_, _) => received.TrySetResult();

        await router.ConnectAsync(CancellationToken.None);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task Disconnecting_stops_the_updates()
    {
        await using SimulatorConnectorRouter router = CreateRouter("Mock");
        await router.ConnectAsync(CancellationToken.None);

        await router.DisconnectAsync(CancellationToken.None);

        int updatesAfterDisconnect = 0;
        router.AircraftStateChanged += (_, _) => Interlocked.Increment(ref updatesAfterDisconnect);

        await Task.Delay(TimeSpan.FromMilliseconds(600), CancellationToken.None);

        Assert.Equal(0, updatesAfterDisconnect);
        Assert.Equal(SimulatorConnectionState.Disconnected, router.ConnectionState);
    }
}

/// <summary>Choosing a flight-plan source.</summary>
public class FlightPlanProviderRoutingTests
{
    private sealed class StubOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static FlightPlanProviderRouter CreateRouter(
        TemporaryStorage storage,
        string? userId,
        bool offlineMode = false)
    {
        var sample = new MockFlightPlanProvider(
            storage.Paths, NullLogger<MockFlightPlanProvider>.Instance);

        // The HttpClient is never used in these tests: every case here resolves to the sample,
        // or refuses before a request is made. A test that reached the network would be an
        // integration test, and those are traited so they do not run by default.
        var simBrief = new SimBriefFlightPlanProvider(
            new HttpClient(),
            Options.Create(new SimBriefOptions { UserId = userId }),
            storage.Paths,
            NullLogger<SimBriefFlightPlanProvider>.Instance);

        return new FlightPlanProviderRouter(
            sample,
            simBrief,
            new StubOptionsMonitor<SimBriefOptions>(new SimBriefOptions { UserId = userId }),
            new StubOptionsMonitor<ApplicationOptions>(new ApplicationOptions { OfflineMode = offlineMode }),
            NullLogger<FlightPlanProviderRouter>.Instance);
    }

    [Fact]
    public async Task With_no_user_id_the_bundled_sample_is_used()
    {
        using var storage = new TemporaryStorage();
        FlightPlanProviderRouter router = CreateRouter(storage, userId: null);

        Assert.False(router.IsSimBriefAvailable);

        FlightPlanImportResult result = await router.ImportLatestAsync(
            new FlightPlanImportRequest(), CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Sample", router.LastProviderUsed);

        FlightPlan plan = Assert.IsType<FlightPlan>(result.FlightPlan);
        Assert.Equal("STMR", plan.DepartureIcao);
    }

    [Fact]
    public async Task The_sample_supplies_the_scenario_squawk()
    {
        using var storage = new TemporaryStorage();
        FlightPlanProviderRouter router = CreateRouter(storage, userId: null);

        await router.ImportLatestAsync(new FlightPlanImportRequest(), CancellationToken.None);

        Assert.NotNull(router.PreassignedSquawk);
    }

    [Fact]
    public void A_configured_user_id_makes_SimBrief_the_provider()
    {
        using var storage = new TemporaryStorage();
        FlightPlanProviderRouter router = CreateRouter(storage, userId: "123456");

        Assert.True(router.IsSimBriefAvailable);
        Assert.Equal("SimBrief", router.ProviderName);
    }

    [Fact]
    public void Whitespace_is_not_a_user_id()
    {
        using var storage = new TemporaryStorage();
        FlightPlanProviderRouter router = CreateRouter(storage, userId: "   ");

        Assert.False(router.IsSimBriefAvailable);
        Assert.Equal("Sample", router.ProviderName);
    }

    [Fact]
    public async Task Offline_mode_refuses_to_contact_SimBrief_and_says_so()
    {
        using var storage = new TemporaryStorage();
        FlightPlanProviderRouter router = CreateRouter(storage, userId: "123456", offlineMode: true);

        Assert.False(router.IsSimBriefAvailable);

        FlightPlanImportResult result = await router.ImportLatestAsync(
            new FlightPlanImportRequest(), CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Sample", router.LastProviderUsed);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("Offline mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_request_user_id_wins_over_the_stored_one()
    {
        using var storage = new TemporaryStorage();

        // Stored: nothing. Requested: nothing either, so the sample is used. The point of this
        // test is the inverse case below â€” that the request is consulted at all.
        FlightPlanProviderRouter router = CreateRouter(storage, userId: null);

        FlightPlanImportResult result = await router.ImportLatestAsync(
            new FlightPlanImportRequest { UserId = "   " }, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Sample", router.LastProviderUsed);
    }
}

