using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StormATC.Desktop.Services;
using StormATC.Desktop.ViewModels;
using StormATC.Desktop.Views;
using StormATC.Infrastructure.DependencyInjection;

namespace StormATC.Desktop;

/// <summary>
/// Application entry point. Builds the Generic Host, wires dependency injection, and shows the
/// main window.
/// </summary>
/// <remarks>
/// <para>
/// StormATC uses <see cref="IHost"/> rather than hand-rolled construction so that configuration,
/// options validation, logging, and <c>IHttpClientFactory</c> behave the same way they would in
/// any other .NET application — and so background services get orderly startup and shutdown.
/// </para>
/// <para>
/// The application must start successfully with no API keys, no simulator, and no network. Any
/// startup step that could fail because a provider is unconfigured is either optional or
/// degrades to a mock.
/// </para>
/// </remarks>
/// <remarks>
/// The base class is spelled out in full: this assembly also references the
/// <c>StormATC.Application</c> namespace, and because this file sits in <c>StormATC.Desktop</c>
/// an unqualified <c>Application</c> would bind to that namespace rather than to WPF's type.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF's Application is not IDisposable; the watchdog is disposed in OnExit.")]
public partial class App : System.Windows.Application
{
    private IHost? _host;
    private StormATC.Desktop.Services.UiResponsivenessWatchdog? _watchdog;

    /// <summary>The service provider, for the few places that need it after startup.</summary>
    public IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("The host has not been built yet.");

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Unhandled exceptions must show a readable message rather than a silent disappearance.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // The first-run screen is shown before the main window exists. Under the default
        // OnLastWindowClose policy, closing it — including with the title-bar X — would be the
        // last window closing, and WPF would shut the application down before it ever appeared.
        // Ownership of shutdown is taken explicitly until the main window is up.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _host = BuildHost(e.Args);
            await _host.StartAsync().ConfigureAwait(true);

            // A clear boundary in the log file, so a report covering several runs can be read.
            _host.Services.GetRequiredService<ILogger<App>>().LogInformation(
                "StormATC {Version} starting. Flight simulation only.",
                MainWindowViewModel.VersionText);

            // Listens for the flight plans the pilot makes in the simulator from the start, so a
            // plan activated before any window opens is not missed.
            _host.Services.GetRequiredService<StormATC.Desktop.Services.SimFlightPlanService>();
            _host.Services.GetRequiredService<StormATC.Desktop.Services.StartPositionService>();
            _host.Services.GetRequiredService<StormATC.Desktop.Services.CloseWithMsfsService>();

            _watchdog = new StormATC.Desktop.Services.UiResponsivenessWatchdog(
                Dispatcher,
                _host.Services.GetRequiredService<ILogger<App>>());

            // The scope statement and the cloud-services cost notice are shown before anything
            // else the first time StormATC runs. Declining is a valid outcome: the application
            // is fully usable with no cloud services at all.
            await ShowFirstRunIfNeededAsync().ConfigureAwait(true);

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = _host.Services.GetRequiredService<MainWindowViewModel>();
            MainWindow = mainWindow;
            mainWindow.Show();

            // From here the main window owns the application's lifetime again.
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            // Kick off session startup after the window is visible, so a slow provider never
            // delays the first paint.
            await _host.Services.GetRequiredService<ApplicationStartupService>()
                .InitializeAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                StormATC.Application.Localization.UiText.T($"StormATC could not start.\n\n{ex.Message}"),
                StormATC.Application.Localization.UiText.T("StormATC — startup failure"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    /// <inheritdoc />
    protected override async void OnExit(ExitEventArgs e)
    {
        _watchdog?.Dispose();

        if (_host is not null)
        {
            try
            {
                _host.Services.GetService<ILogger<App>>()?.LogInformation("StormATC shutting down.");

                await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutdown is best effort; nothing useful can be done if it fails here.
            }

            _host.Dispose();
            _host = null;
        }

        base.OnExit(e);
    }

    /// <summary>Shows the first-run screen when the user has not completed it.</summary>
    private async Task ShowFirstRunIfNeededAsync()
    {
        if (_host is null)
        {
            return;
        }

        // Fully qualified: the WPF base class is System.Windows.Application, so importing
        // StormATC.Application.Options here would make 'Application' ambiguous.
        var options = _host.Services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<StormATC.Application.Options.ApplicationOptions>>();

        if (options.Value.FirstRunCompleted)
        {
            return;
        }

        const string sectionName = StormATC.Application.Options.ApplicationOptions.SectionName;

        var window = _host.Services.GetRequiredService<FirstRunWindow>();
        window.ShowDialog();

        var userSettings = _host.Services.GetRequiredService<UserSettingsService>();

        await userSettings.SaveManyAsync(
            [
                (sectionName, "FirstRunCompleted", System.Text.Json.Nodes.JsonValue.Create(true)),
                (sectionName, "CloudServicesConsentGiven",
                    System.Text.Json.Nodes.JsonValue.Create(window.CloudServicesAccepted)),
            ],
            CancellationToken.None).ConfigureAwait(true);
    }

    private static IHost BuildHost(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // Configuration files sit beside the executable. The content root would otherwise be
        // the working directory, which differs when launched from a shortcut.
        builder.Configuration.SetBasePath(AppContext.BaseDirectory);
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        builder.Configuration.AddJsonFile(
            $"appsettings.{builder.Environment.EnvironmentName}.json",
            optional: true,
            reloadOnChange: true);

        // User settings override the shipped defaults and survive an upgrade.
        string userSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StormATC",
            "usersettings.json");

        // The installer downloads Whisper, Piper and the voices to a standard folder: point the
        // settings at them on the first start, so no path has to be typed.
        StormATC.Infrastructure.LocalVoice.LocalVoiceBootstrap.Apply(
            userSettingsPath,
            StormATC.Infrastructure.LocalVoice.LocalVoiceBootstrap.DefaultRoot);

        builder.Configuration.AddJsonFile(userSettingsPath, optional: true, reloadOnChange: true);

        builder.Configuration.AddEnvironmentVariables(prefix: "STORMATC_");
        builder.Configuration.AddCommandLine(args);

        // The interface language is fixed for the life of the process: it is chosen before the
        // first window exists, and changing it in Settings takes effect at the next start.
        StormATC.Application.Localization.UiText.SetLanguage(
            StormATC.Application.Localization.UiLanguages.Resolve(
                builder.Configuration[
                    $"{StormATC.Application.Options.ApplicationOptions.SectionName}:InterfaceLanguage"]));
        StormATC.Desktop.Localization.XamlTextTranslator.Register();

        // Courtesy and cadence words ("reçu" or "bien reçu") vary so a long session does not sound
        // scripted. Instructions and values never vary; tests keep the reproducible first wording.
        StormATC.Domain.Phraseology.FrenchAtcPhraseology.Instance.VariantPicker =
            StormATC.Domain.Phraseology.RandomVariantPicker.Instance;

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

        // A rolling file log, because a shipped desktop application cannot be told to reproduce
        // the problem with a debugger attached. The provider is constructed here rather than
        // resolved from the container: logging has to work from the very first line, before any
        // service exists to fail while being built.
        var storagePaths = new StormATC.Infrastructure.Storage.AppStoragePaths();

        int retentionDays = builder.Configuration
            .GetSection(StormATC.Application.Options.DiagnosticsOptions.SectionName)
            .GetValue("LogRetentionDays", defaultValue: 7);

        builder.Logging.AddProvider(
            new StormATC.Infrastructure.Logging.RollingFileLoggerProvider(
                storagePaths.LogDirectory, retentionDays));

        builder.Services.AddStormAtcServices(builder.Configuration);
        builder.Services.AddStormAtcDesktop();

        return builder.Build();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ILogger<App>? logger = _host?.Services.GetService<ILogger<App>>();
        logger?.LogError(e.Exception, "An unhandled exception reached the dispatcher.");

        MessageBox.Show(
            StormATC.Application.Localization.UiText.T(
                $"Something went wrong.\n\n{e.Exception.Message}\n\nStormATC will try to continue. See the Diagnostics page for details."),
            StormATC.Application.Localization.UiText.T("StormATC — unexpected error"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        // Keep the application alive: an error in one page should not end a session in progress.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        ILogger<App>? logger = _host?.Services.GetService<ILogger<App>>();

        if (e.ExceptionObject is Exception exception)
        {
            logger?.LogCritical(exception, "An unhandled exception terminated the application.");
        }
    }
}
