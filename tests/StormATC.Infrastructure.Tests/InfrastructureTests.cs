using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Airports;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.Data;
using StormATC.Infrastructure.Persistence;
using StormATC.Infrastructure.Prompts;
using StormATC.Infrastructure.Security;
using StormATC.Infrastructure.SimBrief;
using StormATC.Infrastructure.Storage;

namespace StormATC.Infrastructure.Tests;

/// <summary>
/// Paths rooted at a temporary directory, so no test touches the real user profile.
/// </summary>
internal sealed class TemporaryStorage : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "stormatc-tests", Guid.CreateVersion7().ToString("N"));

    internal TemporaryStorage()
    {
        Paths = new AppStoragePaths(_root, AppContext.BaseDirectory);
        Paths.EnsureCreated();
    }

    internal AppStoragePaths Paths { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file still held open by SQLite's connection pool is not worth failing a test.
        }
    }
}

/// <summary>Parsing a SimBrief OFP, including every way it can be unusable.</summary>
public class SimBriefParsingTests
{
    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void A_well_formed_OFP_parses_into_a_flight_plan()
    {
        FlightPlanImportResult result = SimBriefFlightPlanProvider.ParseOfp(
            LoadFixture("simbrief-ofp-sample.xml"));

        Assert.True(result.Success, result.ErrorMessage);

        FlightPlan plan = Assert.IsType<FlightPlan>(result.FlightPlan);
        Assert.Equal("STM123", plan.Callsign.Normalized);
        Assert.Equal("STMR", plan.DepartureIcao);
        Assert.Equal("STMD", plan.DestinationIcao);
        Assert.Equal("A320", plan.AircraftIcaoType);
        Assert.Equal(RunwayIdentifier.Create("18"), plan.DepartureRunway);
        Assert.Equal(RunwayIdentifier.Create("36"), plan.ArrivalRunway);
        Assert.Equal("STORM1A", plan.StandardInstrumentDeparture);
        Assert.Equal(12_000, plan.CruiseAltitude?.Feet);
        Assert.Contains("STMA", plan.AlternateIcaos);
        Assert.Equal("SimBrief", plan.SourceIdentifier);
    }

    [Fact]
    public void The_navlog_becomes_route_legs_in_order()
    {
        FlightPlanImportResult result = SimBriefFlightPlanProvider.ParseOfp(
            LoadFixture("simbrief-ofp-sample.xml"));

        IReadOnlyList<RouteLeg> legs = result.FlightPlan!.RouteLegs;

        Assert.Equal(["STMR", "KILOX", "NOVAR", "STMD"], legs.Select(leg => leg.Identifier));
        Assert.Equal("STORM1A", legs[1].Via);
        Assert.NotNull(legs[1].Position);
    }

    [Fact]
    public void A_cruise_below_the_transition_stays_an_altitude()
    {
        // 12 000 ft is spoken as an altitude, not as a flight level. Getting this wrong makes
        // every clearance readback comparison fail.
        FlightPlanImportResult result = SimBriefFlightPlanProvider.ParseOfp(
            LoadFixture("simbrief-ofp-sample.xml"));

        Assert.False(result.FlightPlan!.CruiseAltitude!.Value.IsFlightLevel);
    }

    [Fact]
    public void Malformed_XML_fails_with_something_the_user_can_act_on()
    {
        FlightPlanImportResult result = SimBriefFlightPlanProvider.ParseOfp("<OFP><general>");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.False(string.IsNullOrWhiteSpace(result.SuggestedAction));
    }

    [Fact]
    public void An_empty_response_fails_rather_than_producing_an_empty_plan()
    {
        FlightPlanImportResult result = SimBriefFlightPlanProvider.ParseOfp("<OFP />");

        Assert.False(result.Success);
        Assert.Null(result.FlightPlan);
    }

    [Fact]
    public void A_SimBrief_error_status_is_surfaced_verbatim()
    {
        FlightPlanImportResult result = SimBriefFlightPlanProvider.ParseOfp(
            "<OFP><fetch><status>Error: Unknown UserID</status></fetch></OFP>");

        Assert.False(result.Success);
        Assert.Contains("Unknown UserID", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_missing_its_aerodromes_is_refused()
    {
        FlightPlanImportResult result = SimBriefFlightPlanProvider.ParseOfp(
            "<OFP><general><icao_airline>STM</icao_airline></general></OFP>");

        Assert.False(result.Success);
    }

    [Fact]
    public void A_missing_navlog_is_a_warning_not_a_failure()
    {
        FlightPlanImportResult result = SimBriefFlightPlanProvider.ParseOfp(
            """
            <OFP>
              <general><icao_airline>STM</icao_airline><flight_number>123</flight_number>
                <initial_altitude>12000</initial_altitude></general>
              <origin><icao_code>STMR</icao_code><plan_rwy>18</plan_rwy></origin>
              <destination><icao_code>STMD</icao_code><plan_rwy>36</plan_rwy></destination>
            </OFP>
            """);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Warnings);
        Assert.Empty(result.FlightPlan!.RouteLegs);
    }
}

/// <summary>Reading the bundled aerodrome files.</summary>
public class JsonAirportDataProviderTests
{
    private static JsonAirportDataProvider Provider()
    {
        var paths = new AppStoragePaths(
            Path.Combine(Path.GetTempPath(), "stormatc-tests-readonly"), AppContext.BaseDirectory);

        return new JsonAirportDataProvider(paths, NullLogger<JsonAirportDataProvider>.Instance);
    }

    [Fact]
    public async Task The_bundled_departure_aerodrome_loads()
    {
        AirportData? airport = await Provider().GetAirportAsync("STMR", CancellationToken.None);

        Assert.NotNull(airport);
        Assert.Equal("STMR", airport.Icao);
        Assert.True(airport.IsFictional);
        Assert.NotEmpty(airport.Runways);
        Assert.NotEmpty(airport.Stands);
        Assert.True(airport.SupportsTaxiRouting);
    }

    [Fact]
    public async Task Every_bundled_aerodrome_is_marked_fictional()
    {
        // Sample data must never be mistaken for navigation data.
        JsonAirportDataProvider provider = Provider();

        foreach (string icao in await provider.ListAvailableAirportsAsync(CancellationToken.None))
        {
            AirportData? airport = await provider.GetAirportAsync(icao, CancellationToken.None);
            Assert.True(airport?.IsFictional, $"{icao} is not marked fictional.");
        }
    }

    [Fact]
    public async Task The_departure_aerodrome_publishes_the_frequencies_the_session_needs()
    {
        AirportData airport = (await Provider().GetAirportAsync("STMR", CancellationToken.None))!;

        Assert.NotNull(airport.Frequencies.ClearanceDelivery);
        Assert.NotNull(airport.Frequencies.Ground);
        Assert.NotNull(airport.Frequencies.Tower);
        Assert.NotNull(airport.Frequencies.Departure);
    }

    [Fact]
    public async Task An_unknown_aerodrome_returns_null_rather_than_a_placeholder()
    {
        Assert.Null(await Provider().GetAirportAsync("ZZZZ", CancellationToken.None));
    }

    [Fact]
    public async Task The_taxi_graph_resolves_a_route_from_the_stand_to_the_runway()
    {
        AirportData airport = (await Provider().GetAirportAsync("STMR", CancellationToken.None))!;

        TaxiRoute route = TaxiPathfinder.FindRouteToHoldingPoint(
            airport, "A1", RunwayIdentifier.Create("18"));

        Assert.True(route.Found);
        Assert.NotEmpty(route.TaxiwayNames);
    }
}

/// <summary>Local storage: schema, round-trips, and deletion.</summary>
public class SqliteSessionRepositoryTests
{
    private static SqliteSessionRepository Repository(TemporaryStorage storage) => new(
        storage.Paths,
        Options.Create(new PersistenceOptions()),
        NullLogger<SqliteSessionRepository>.Instance);

    private static AtcSession NewSession() => AtcSession.Create(
        Guid.CreateVersion7(),
        DateTimeOffset.UtcNow,
        SessionMode.Mock,
        AircraftState.Unknown(DateTimeOffset.UtcNow))
        with
    {
        FlightPlan = new FlightPlan
        {
            FlightPlanId = "test",
            Callsign = Callsign.Create("STORM123"),
            DepartureIcao = "STMR",
            DestinationIcao = "STMD",
            SourceIdentifier = "Test",
        },
    };

    [Fact]
    public async Task Initialising_creates_the_schema_at_the_current_version()
    {
        using var storage = new TemporaryStorage();
        using SqliteSessionRepository repository = Repository(storage);

        await repository.InitializeAsync(CancellationToken.None);

        Assert.Equal(1, await repository.GetSchemaVersionAsync(CancellationToken.None));
        Assert.True(File.Exists(storage.Paths.DatabasePath));
    }

    [Fact]
    public async Task Initialising_twice_is_safe()
    {
        using var storage = new TemporaryStorage();
        using SqliteSessionRepository repository = Repository(storage);

        await repository.InitializeAsync(CancellationToken.None);
        await repository.InitializeAsync(CancellationToken.None);

        Assert.Equal(1, await repository.GetSchemaVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_session_and_its_conversation_round_trip()
    {
        using var storage = new TemporaryStorage();
        using SqliteSessionRepository repository = Repository(storage);
        await repository.InitializeAsync(CancellationToken.None);

        AtcSession session = NewSession();
        await repository.SaveSessionAsync(session, CancellationToken.None);

        await repository.SaveConversationTurnAsync(
            session.SessionId,
            new ConversationTurn
            {
                TurnId = Guid.CreateVersion7(),
                Timestamp = DateTimeOffset.UtcNow,
                Speaker = ConversationSpeaker.Pilot,
                Controller = ControllerPosition.ClearanceDelivery,
                Text = "storm one two three request ifr clearance",
            },
            CancellationToken.None);

        IReadOnlyList<ConversationTurn> turns =
            await repository.LoadConversationAsync(session.SessionId, CancellationToken.None);

        Assert.Single(turns);
        Assert.Equal(ConversationSpeaker.Pilot, turns[0].Speaker);
        Assert.Contains("clearance", turns[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_the_same_session_twice_updates_rather_than_duplicates()
    {
        using var storage = new TemporaryStorage();
        using SqliteSessionRepository repository = Repository(storage);
        await repository.InitializeAsync(CancellationToken.None);

        AtcSession session = NewSession();
        await repository.SaveSessionAsync(session, CancellationToken.None);
        await repository.SaveSessionAsync(session.WithPhase(FlightPhase.TaxiOut), CancellationToken.None);

        IReadOnlyList<SessionSummary> sessions =
            await repository.ListSessionsAsync(10, CancellationToken.None);

        Assert.Single(sessions);
    }

    [Fact]
    public async Task The_session_list_carries_what_the_transcript_page_shows()
    {
        using var storage = new TemporaryStorage();
        using SqliteSessionRepository repository = Repository(storage);
        await repository.InitializeAsync(CancellationToken.None);

        await repository.SaveSessionAsync(NewSession(), CancellationToken.None);

        SessionSummary summary = (await repository.ListSessionsAsync(10, CancellationToken.None))[0];

        Assert.Equal("STORM123", summary.Callsign);
        Assert.Equal("STMR", summary.Departure);
        Assert.Equal("STMD", summary.Destination);
    }

    [Fact]
    public async Task Clearing_history_removes_everything()
    {
        using var storage = new TemporaryStorage();
        using SqliteSessionRepository repository = Repository(storage);
        await repository.InitializeAsync(CancellationToken.None);

        AtcSession session = NewSession();
        await repository.SaveSessionAsync(session, CancellationToken.None);
        await repository.SaveConversationTurnAsync(
            session.SessionId,
            new ConversationTurn
            {
                TurnId = Guid.CreateVersion7(),
                Timestamp = DateTimeOffset.UtcNow,
                Speaker = ConversationSpeaker.Pilot,
                Text = "anything",
            },
            CancellationToken.None);

        await repository.ClearAllHistoryAsync(CancellationToken.None);

        Assert.Empty(await repository.ListSessionsAsync(10, CancellationToken.None));
        Assert.Empty(await repository.LoadConversationAsync(session.SessionId, CancellationToken.None));
    }
}

/// <summary>Secret handling. The values must never be recoverable from anything but the store.</summary>
public class SecretStoreTests
{
    [Fact]
    public async Task A_stored_secret_round_trips()
    {
        using var storage = new TemporaryStorage();
        using var store = new DpapiSecretStore(storage.Paths, NullLogger<DpapiSecretStore>.Instance);

        await store.SetSecretAsync(SecretKeys.OpenRouterApiKey, "test-value-not-a-real-key", CancellationToken.None);

        Assert.True(await store.HasSecretAsync(SecretKeys.OpenRouterApiKey, CancellationToken.None));
        Assert.Equal(
            "test-value-not-a-real-key",
            await store.GetSecretAsync(SecretKeys.OpenRouterApiKey, CancellationToken.None));
    }

    [Fact]
    public async Task A_removed_secret_is_gone()
    {
        using var storage = new TemporaryStorage();
        using var store = new DpapiSecretStore(storage.Paths, NullLogger<DpapiSecretStore>.Instance);

        await store.SetSecretAsync(SecretKeys.DeepgramApiKey, "test-value", CancellationToken.None);
        await store.RemoveSecretAsync(SecretKeys.DeepgramApiKey, CancellationToken.None);

        Assert.False(await store.HasSecretAsync(SecretKeys.DeepgramApiKey, CancellationToken.None));
        Assert.Null(await store.GetSecretAsync(SecretKeys.DeepgramApiKey, CancellationToken.None));
    }

    [Fact]
    public async Task An_absent_secret_returns_null_rather_than_throwing()
    {
        using var storage = new TemporaryStorage();
        using var store = new DpapiSecretStore(storage.Paths, NullLogger<DpapiSecretStore>.Instance);

        Assert.Null(await store.GetSecretAsync("never.stored", CancellationToken.None));
    }

    [Fact]
    public async Task The_secrets_file_never_contains_the_value_in_the_clear()
    {
        using var storage = new TemporaryStorage();
        using var store = new DpapiSecretStore(storage.Paths, NullLogger<DpapiSecretStore>.Instance);

        const string Secret = "sk-test-abcdefghijklmnop-not-a-real-key";
        await store.SetSecretAsync(SecretKeys.OpenRouterApiKey, Secret, CancellationToken.None);

        foreach (string file in Directory.EnumerateFiles(
            storage.Paths.ApplicationDataDirectory, "*", SearchOption.AllDirectories))
        {
            string contents = await File.ReadAllTextAsync(file, CancellationToken.None);
            Assert.DoesNotContain(Secret, contents, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("sk-test-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("short")]
    public void Masking_never_reveals_the_middle_of_a_key(string secret)
    {
        string masked = DpapiSecretStore.Mask(secret);

        Assert.DoesNotContain(secret, masked, StringComparison.Ordinal);
        Assert.Contains('•', masked);
    }

    [Fact]
    public void A_short_secret_is_masked_entirely()
    {
        // Showing four of eight characters would leak most of the value.
        string masked = DpapiSecretStore.Mask("abcdefgh");

        Assert.DoesNotContain('a', masked);
        Assert.All(masked, character => Assert.Equal('•', character));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Masking_nothing_produces_nothing(string? secret) =>
        Assert.Equal(string.Empty, DpapiSecretStore.Mask(secret));

    [Fact]
    public async Task The_in_memory_store_behaves_the_same_way()
    {
        var store = new InMemorySecretStore();

        Assert.False(await store.HasSecretAsync(SecretKeys.OpenRouterApiKey, CancellationToken.None));

        await store.SetSecretAsync(SecretKeys.OpenRouterApiKey, "value", CancellationToken.None);
        Assert.True(await store.HasSecretAsync(SecretKeys.OpenRouterApiKey, CancellationToken.None));

        await store.RemoveSecretAsync(SecretKeys.OpenRouterApiKey, CancellationToken.None);
        Assert.False(await store.HasSecretAsync(SecretKeys.OpenRouterApiKey, CancellationToken.None));
    }
}

/// <summary>Loading controller prompts from disk.</summary>
public class FilePromptCatalogTests
{
    private static FilePromptCatalog Catalog()
    {
        var paths = new AppStoragePaths(
            Path.Combine(Path.GetTempPath(), "stormatc-tests-prompts"), AppContext.BaseDirectory);

        return new FilePromptCatalog(paths, NullLogger<FilePromptCatalog>.Instance);
    }

    [Fact]
    public async Task Every_expected_prompt_is_present_in_the_build_output()
    {
        FilePromptCatalog catalog = Catalog();

        foreach (string name in PromptNames.All)
        {
            Assert.True(
                await catalog.HasPromptAsync(name, CancellationToken.None),
                $"Prompt '{name}' is missing.");
        }

        Assert.Empty(catalog.MissingPrompts);
    }

    [Fact]
    public async Task A_prompt_loads_with_content()
    {
        string prompt = await Catalog().GetPromptAsync(
            PromptNames.GlobalControllerRules, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(prompt));
    }

    [Fact]
    public async Task A_missing_prompt_is_recorded_rather_than_thrown()
    {
        FilePromptCatalog catalog = Catalog();

        string prompt = await catalog.GetPromptAsync("99-does-not-exist", CancellationToken.None);

        Assert.Equal(string.Empty, prompt);
        Assert.Contains("99-does-not-exist", catalog.MissingPrompts);
    }

    [Fact]
    public async Task Prompt_hashes_are_stable_and_short()
    {
        FilePromptCatalog catalog = Catalog();

        IReadOnlyDictionary<string, string> first =
            await catalog.GetPromptHashesAsync(CancellationToken.None);

        IReadOnlyDictionary<string, string> second =
            await catalog.GetPromptHashesAsync(CancellationToken.None);

        Assert.NotEmpty(first);
        Assert.Equal(first[PromptNames.TowerController], second[PromptNames.TowerController]);
        Assert.Equal(12, first[PromptNames.TowerController].Length);
    }
}

/// <summary>Credential destinations and defensive log redaction.</summary>
public class ProviderSecurityTests
{
    [Theory]
    [InlineData("https://openrouter.ai/api/v1")]
    [InlineData("https://openrouter.ai/api/v1/")]
    public void Official_openrouter_endpoints_are_allowed(string endpoint) =>
        Assert.True(ProviderEndpointPolicy.IsAllowedOpenRouter(endpoint));

    [Theory]
    [InlineData("http://openrouter.ai/api/v1")]
    [InlineData("https://openrouter.ai.evil.example/api/v1")]
    [InlineData("https://openrouter.ai:8443/api/v1")]
    [InlineData("https://user@openrouter.ai/api/v1")]
    [InlineData("https://openrouter.ai/api/v1?redirect=evil")]
    public void Credential_redirects_are_blocked(string endpoint) =>
        Assert.False(ProviderEndpointPolicy.IsAllowedOpenRouter(endpoint));

    [Fact]
    public void Official_deepgram_endpoints_are_allowed()
    {
        Assert.True(ProviderEndpointPolicy.IsAllowedDeepgramRest("https://api.deepgram.com"));
        Assert.True(ProviderEndpointPolicy.IsAllowedDeepgramStreaming(
            "wss://api.deepgram.com/v1/listen"));
    }

    [Fact]
    public void Redactor_removes_headers_fields_and_provider_keys()
    {
        const string Input =
            "Authorization: Bearer sk-test-abcdefghijklmnop; "
            + "\"api_key\":\"very-secret-value\" key=sk-or-v1-test-abcdefghijklmnop";

        string redacted = SensitiveDataRedactor.Redact(Input);

        Assert.DoesNotContain("sk-test-", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret-value", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-or-v1-", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }
}
