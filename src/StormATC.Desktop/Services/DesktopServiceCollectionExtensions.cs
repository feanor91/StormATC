using Microsoft.Extensions.DependencyInjection;
using StormATC.Application.Abstractions;
using StormATC.Application.Atis;
using StormATC.Desktop.Services;
using StormATC.Desktop.ViewModels;
using StormATC.Desktop.Views;

namespace StormATC.Desktop;

/// <summary>
/// Registers the desktop layer: the shell, the pages, and the services that exist only because
/// there is a user interface.
/// </summary>
/// <remarks>
/// View models are singletons because the pages are constructed once and kept alive, and because
/// several of them subscribe to session events — a transient view model would leak subscriptions
/// on every navigation.
/// </remarks>
public static class DesktopServiceCollectionExtensions
{
    /// <summary>Registers the window, the view models, and the desktop-only services.</summary>
    public static IServiceCollection AddStormAtcDesktop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ProviderStatusService>();
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton<IVoiceRejection, VoiceRejectionService>();
        services.AddSingleton<StormATC.Application.Charts.IVacPageRenderer, WindowsPdfPageRenderer>();
        services.AddSingleton<SimFlightPlanService>();
        services.AddSingleton<ISimRouteReceiver>(sp => sp.GetRequiredService<SimFlightPlanService>());
        services.AddSingleton<StartPositionService>();
        services.AddSingleton<AtisBroadcastService>();
        services.AddSingleton<ApplicationStartupService>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<FlightPlanViewModel>();
        services.AddSingleton<RadioViewModel>();
        services.AddSingleton<TranscriptViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        services.AddSingleton<MainWindow>();
        services.AddTransient<FirstRunWindow>();

        return services;
    }
}
