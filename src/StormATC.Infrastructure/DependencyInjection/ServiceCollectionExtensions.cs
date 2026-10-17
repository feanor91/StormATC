using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Options;
using StormATC.Application.Panel;
using StormATC.Application.Phraseology;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Domain.Atc;
using StormATC.Domain.Readback;
using StormATC.Infrastructure.Data;
using StormATC.Infrastructure.FlightPlanning;
using StormATC.Infrastructure.Input;
using StormATC.Infrastructure.Mocks;
using StormATC.Infrastructure.Panel;
using StormATC.Infrastructure.Persistence;
using StormATC.Infrastructure.Prompts;
using StormATC.Infrastructure.Security;
using StormATC.Infrastructure.SimBrief;
using StormATC.Infrastructure.Simulator;
using StormATC.Infrastructure.Storage;

namespace StormATC.Infrastructure.DependencyInjection;

/// <summary>
/// Registers every StormATC service.
/// </summary>
/// <remarks>
/// <para>
/// Provider selection happens here and nowhere else. Each provider is chosen on <b>its own</b>
/// configuration rather than one global mock switch: a simulator is used if one is running, a
/// SimBrief plan is fetched if a user ID is set, a model is called if a key and a model name are
/// stored. Those are independent questions, and answering them together used to mean that
/// enabling any one of them enabled all of them.
/// </para>
/// <para>
/// Nothing downstream branches on which implementation won: the coordinator, the engine, and the
/// view models see the same interfaces either way. That is what makes running with no providers
/// at all a genuine test of the real architecture rather than a separate demo path.
/// </para>
/// <para>
/// Every provider degrades honestly. An unconfigured one reports that it is unconfigured; none
/// of them fabricates a result, and none of them prevents startup.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers domain, application, and infrastructure services.</summary>
    public static IServiceCollection AddStormAtcServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddOptions(services, configuration);

        // --- Cross-cutting -----------------------------------------------------------------
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IStoragePaths, AppStoragePaths>();
        services.AddSingleton<IPromptCatalog, FilePromptCatalog>();
        services.AddSingleton<WindowsJoystickInputService>();
        services.AddSingleton<IJoystickInputService>(
            provider => provider.GetRequiredService<WindowsJoystickInputService>());
        services.AddSingleton<IHostedService>(
            provider => provider.GetRequiredService<WindowsJoystickInputService>());

        // --- Domain services ----------------------------------------------------------------
        services.AddSingleton<IReadbackEvaluator, DeterministicReadbackEvaluator>();
        services.AddSingleton<IAtcStateEngine, StormAtcStateEngine>();

        // --- Data providers -----------------------------------------------------------------
        services.AddSingleton<JsonAirportDataProvider>();
        services.AddSingleton<IAirportDataProvider, MsfsAirportDataProvider>();
        services.AddSingleton<IVfrProfileProvider, JsonVfrProfileProvider>();
        services.AddSingleton<IAirspaceDataProvider, JsonAirspaceDataProvider>();
        services.AddSingleton<MockTrafficProvider>();
        services.AddSingleton<MsfsTrafficProvider>();
        services.AddSingleton<ITrafficProvider>(sp => sp.GetRequiredService<MsfsTrafficProvider>());
        services.AddSingleton<MockWeatherProvider>();
        services.AddSingleton<MsfsWeatherProvider>();
        services.AddSingleton<IWeatherProvider>(sp => sp.GetRequiredService<MsfsWeatherProvider>());
        services.AddSingleton<TrafficRadioService>();
        services.AddSingleton<IHostedService>(
            sp => sp.GetRequiredService<TrafficRadioService>());

        // --- Persistence ---------------------------------------------------------------------
        services.AddSingleton<ISessionRepository, SqliteSessionRepository>();

        // --- Application ----------------------------------------------------------------------
        services.AddSingleton<AtcSessionContext>();
        services.AddSingleton<DeterministicPilotIntentParser>();
        services.AddSingleton<PhraseologyValidator>();
        services.AddSingleton<IPhraseologyRenderer, DeterministicPhraseologyRenderer>();

        services.AddSingleton<IPilotIntentParser>(sp =>
        {
            AtcRealismOptions realism = sp.GetRequiredService<IOptions<AtcRealismOptions>>().Value;

            return new EscalatingPilotIntentParser(
                sp.GetRequiredService<DeterministicPilotIntentParser>(),

                // Null until a cloud-backed parser is configured. The escalating parser
                // handles that by returning its deterministic result, which is the correct
                // degraded behaviour rather than a failure.
                sp.GetService<ILanguageModelIntentParser>(),
                realism.LanguageModelEscalationThreshold);
        });

        services.AddSingleton<PilotTransmissionCoordinator>();
        services.AddSingleton<StateChangeMonitor>();
        services.AddSingleton<IToolbarPanelStateSource, SessionToolbarPanelStateSource>();
        services.AddSingleton(sp => new ToolbarPanelServer(
            sp.GetRequiredService<IToolbarPanelStateSource>(),
            sp.GetRequiredService<IOptions<ToolbarPanelOptions>>(),
            sp.GetRequiredService<ILogger<ToolbarPanelServer>>(),
            Path.Combine(AppContext.BaseDirectory, "panel")));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ToolbarPanelServer>());
        services.AddSingleton<RadioActivityState>();

        AddProviders(services, configuration);

        return services;
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ApplicationOptions>()
            .Bind(configuration.GetSection(ApplicationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<VfrLocalOptions>()
            .Bind(configuration.GetSection(VfrLocalOptions.SectionName));

        services.AddOptions<ToolbarPanelOptions>()
            .Bind(configuration.GetSection(ToolbarPanelOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddOptions<OpenRouterOptions>()
            .Bind(configuration.GetSection(OpenRouterOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Security.ProviderEndpointPolicy.IsAllowedOpenRouter(options.BaseUrl),
                "OpenRouter:BaseUrl must be the official https://openrouter.ai/api/v1 endpoint.")
            .ValidateOnStart();

        services.AddOptions<LanguageModelOptions>()
            .Bind(configuration.GetSection(LanguageModelOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => LanguageModelProviders.All.Any(provider =>
                    provider.Equals(options.Provider, StringComparison.OrdinalIgnoreCase)),
                "LanguageModel:Provider must name a supported provider.")
            .ValidateOnStart();

        services.AddOptions<OpenAIOptions>()
            .Bind(configuration.GetSection(OpenAIOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Security.ProviderEndpointPolicy.IsAllowedOpenAI(options.BaseUrl),
                "OpenAI:BaseUrl must be the official https://api.openai.com/v1 endpoint.")
            .ValidateOnStart();

        services.AddOptions<GroqOptions>()
            .Bind(configuration.GetSection(GroqOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Security.ProviderEndpointPolicy.IsAllowedGroq(options.BaseUrl),
                "Groq:BaseUrl must be the official https://api.groq.com/openai/v1 endpoint.")
            .ValidateOnStart();

        services.AddOptions<DeepSeekOptions>()
            .Bind(configuration.GetSection(DeepSeekOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Security.ProviderEndpointPolicy.IsAllowedDeepSeek(options.BaseUrl),
                "DeepSeek:BaseUrl must be the official https://api.deepseek.com endpoint.")
            .ValidateOnStart();

        services.AddOptions<AnthropicOptions>()
            .Bind(configuration.GetSection(AnthropicOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Security.ProviderEndpointPolicy.IsAllowedAnthropic(options.BaseUrl),
                "Anthropic:BaseUrl must be the official https://api.anthropic.com endpoint.")
            .ValidateOnStart();

        services.AddOptions<DeepgramOptions>()
            .Bind(configuration.GetSection(DeepgramOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Security.ProviderEndpointPolicy.IsAllowedDeepgramRest(options.BaseUrl),
                "Deepgram:BaseUrl must be the official https://api.deepgram.com endpoint.")
            .Validate(
                options => Security.ProviderEndpointPolicy.IsAllowedDeepgramStreaming(
                    options.StreamingUrl),
                "Deepgram:StreamingUrl must be the official wss://api.deepgram.com/v1/listen endpoint.")
            .ValidateOnStart();

        services.AddOptions<LocalVoiceOptions>()
            .Bind(configuration.GetSection(LocalVoiceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SimBriefOptions>()
            .Bind(configuration.GetSection(SimBriefOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Security.ProviderEndpointPolicy.IsAllowedSimBrief(options.BaseUrl),
                "SimBrief:BaseUrl must be the official SimBrief OFP endpoint.")
            .ValidateOnStart();

        services.AddOptions<SimulatorOptions>()
            .Bind(configuration.GetSection(SimulatorOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<TrafficOptions>()
            .Bind(configuration.GetSection(TrafficOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.MinimumInterval >= TimeSpan.FromSeconds(30)
                    && options.MinimumInterval <= TimeSpan.FromHours(1),
                "Traffic:MinimumInterval must be between 30 seconds and one hour.")
            .ValidateOnStart();

        services.AddOptions<AudioOptions>()
            .Bind(configuration.GetSection(AudioOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AtcRealismOptions>()
            .Bind(configuration.GetSection(AtcRealismOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<DiagnosticsOptions>()
            .Bind(configuration.GetSection(DiagnosticsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    /// <summary>
    /// Registers the external providers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only configuration read here is <see cref="ApplicationOptions.OfflineMode"/>, and only
    /// because it is a guarantee rather than a preference: with it on, the implementations that
    /// <i>can</i> reach the network are not registered at all. Everything else decides at run
    /// time, from a stored key or an account number, so a setting saved on the Settings page
    /// takes effect without a restart.
    /// </para>
    /// </remarks>
    private static void AddProviders(IServiceCollection services, IConfiguration configuration)
    {
        bool offlineMode = configuration
            .GetSection(ApplicationOptions.SectionName)
            .GetValue("OfflineMode", defaultValue: false);

        // --- Secret store -------------------------------------------------------------------
        // Always the operating system store, in every mode. Bring-your-own-key only works if a
        // key survives a restart, and an in-memory store silently discarded one the user had
        // explicitly saved. No file is written until a key is stored.
        services.AddSingleton<ISecretStore, DpapiSecretStore>();

        // --- Simulator ----------------------------------------------------------------------
        // Both connectors are registered concretely; the router decides which one is live and
        // can change its mind while the application is running. The mock stays reachable by name
        // so the developer controls on the dashboard can drive it directly.
        services.AddSingleton<MockSimulatorConnector>();
        services.AddSingleton<MsfsSimConnectConnector>();
        services.AddSingleton<SimulatorConnectorRouter>();
        services.AddSingleton<ISimulatorConnector>(sp =>
            sp.GetRequiredService<SimulatorConnectorRouter>());

        // --- Flight plan ---------------------------------------------------------------------
        // SimBrief is always available to the router; whether it is used depends on the user ID,
        // which the user can enter and act on immediately.
        services.AddSingleton<MockFlightPlanProvider>();
        services.AddHttpClient<SimBriefFlightPlanProvider>()
            .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });
        services.AddSingleton<FlightPlanProviderRouter>();
        services.AddSingleton<IFlightPlanProvider>(sp =>
            sp.GetRequiredService<FlightPlanProviderRouter>());

        // --- Language model -------------------------------------------------------------------
        if (offlineMode)
        {
            // Not a stub that fakes intelligent answers: it reports NotConfigured, which drives
            // the pipeline down the same deterministic path a real outage produces.
            services.AddSingleton<ILanguageModelClient, MockLanguageModelClient>();
            services.AddSingleton<IModelCatalog, OfflineModelCatalog>();
        }
        else
        {
            services.AddHttpClient<OpenRouter.ModelCatalogClient>()
                .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                });
            services.AddSingleton<IModelCatalog>(sp =>
                sp.GetRequiredService<OpenRouter.ModelCatalogClient>());

            services.AddHttpClient<OpenRouter.OpenRouterClient>()
                .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                });
            services.AddSingleton<ILanguageModelClient>(sp =>
                sp.GetRequiredService<OpenRouter.OpenRouterClient>());

            // The two places a model is permitted to participate. Both check that a key and a
            // model name exist and degrade to the deterministic path when either is absent.
            services.AddSingleton<ILanguageModelIntentParser, OpenRouter.OpenRouterIntentParser>();
            services.AddSingleton<ILanguageModelPhraseologyRenderer, OpenRouter.OpenRouterPhraseologyRenderer>();
        }

        // --- Speech ------------------------------------------------------------------------------
        // The local engines (Whisper, Piper) never use the network, so they are available in
        // every mode. The routers pick local or cloud from Settings at run time; offline mode
        // only replaces the cloud side with the mocks.
        services.AddSingleton<Speech.WhisperSpeechRecognitionService>();
        services.AddSingleton<Speech.PiperTextToSpeechService>();

        if (offlineMode)
        {
            services.AddSingleton<MockSpeechRecognitionService>();
            services.AddSingleton<MockTextToSpeechService>();
            services.AddSingleton<ISpeechRecognitionService>(sp => new Speech.SpeechRecognitionRouter(
                sp.GetRequiredService<MockSpeechRecognitionService>(),
                sp.GetRequiredService<Speech.WhisperSpeechRecognitionService>(),
                sp.GetRequiredService<IOptionsMonitor<LocalVoiceOptions>>()));
            services.AddSingleton<ITextToSpeechService>(sp => new Speech.TextToSpeechRouter(
                sp.GetRequiredService<MockTextToSpeechService>(),
                sp.GetRequiredService<Speech.PiperTextToSpeechService>(),
                sp.GetRequiredService<IOptionsMonitor<LocalVoiceOptions>>()));
        }
        else
        {
            // Both open a connection only when a key is present, and report an actionable error
            // when it is not — so push-to-talk never looks like it worked when it did not.
            services.AddSingleton<Speech.DeepgramSpeechRecognitionService>();
            services.AddHttpClient<Speech.DeepgramTextToSpeechService>()
                .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                });
            services.AddSingleton<ISpeechRecognitionService>(sp => new Speech.SpeechRecognitionRouter(
                sp.GetRequiredService<Speech.DeepgramSpeechRecognitionService>(),
                sp.GetRequiredService<Speech.WhisperSpeechRecognitionService>(),
                sp.GetRequiredService<IOptionsMonitor<LocalVoiceOptions>>()));
            services.AddSingleton<ITextToSpeechService>(sp => new Speech.TextToSpeechRouter(
                sp.GetRequiredService<Speech.DeepgramTextToSpeechService>(),
                sp.GetRequiredService<Speech.PiperTextToSpeechService>(),
                sp.GetRequiredService<IOptionsMonitor<LocalVoiceOptions>>()));
        }
    }
}
