using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StormATC.Desktop.ViewModels;

/// <summary>
/// The shell view model: owns navigation and the pages.
/// </summary>
/// <remarks>
/// Pages are constructed once at startup and kept alive, so switching pages preserves scroll
/// position and in-progress input. There are six of them and none holds significant memory, so
/// the simplicity is worth more than lazy construction.
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableObject _currentPage;

    [ObservableProperty]
    private string _currentPageTitle = "Dashboard";

    [ObservableProperty]
    private string _currentPageDescription = "Live flight, radio and controller guidance";

    /// <summary>Creates the shell view model with every page.</summary>
    public MainWindowViewModel(
        DashboardViewModel dashboard,
        FlightPlanViewModel flightPlan,
        RadioViewModel radio,
        TranscriptViewModel transcript,
        SettingsViewModel settings,
        DiagnosticsViewModel diagnostics)
    {
        Dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        FlightPlan = flightPlan ?? throw new ArgumentNullException(nameof(flightPlan));
        Radio = radio ?? throw new ArgumentNullException(nameof(radio));
        Transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

        Pages =
        [
            new NavigationEntry(
                "Dashboard",
                "Live flight and guidance",
                "\uE80F",
                "Ctrl+1",
                dashboard),
            new NavigationEntry(
                "Flight Plan",
                "Route and clearance",
                "\uE707",
                "Ctrl+2",
                flightPlan),
            new NavigationEntry(
                "Radio",
                "Frequencies and handoffs",
                "\uE702",
                "Ctrl+3",
                radio),
            new NavigationEntry(
                "Transcript",
                "Live and stored sessions",
                "\uE8A5",
                "Ctrl+4",
                transcript),
            new NavigationEntry(
                "Settings",
                "Voice, controls and privacy",
                "\uE713",
                "Ctrl+5",
                settings),
            new NavigationEntry(
                "Diagnostics",
                "Health and troubleshooting",
                "\uE9D9",
                "Ctrl+6",
                diagnostics),
        ];

        _currentPage = dashboard;
    }

    /// <summary>The dashboard page.</summary>
    public DashboardViewModel Dashboard { get; }

    /// <summary>The flight plan page.</summary>
    public FlightPlanViewModel FlightPlan { get; }

    /// <summary>The radio page.</summary>
    public RadioViewModel Radio { get; }

    /// <summary>The transcript page.</summary>
    public TranscriptViewModel Transcript { get; }

    /// <summary>The settings page.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The diagnostics page.</summary>
    public DiagnosticsViewModel Diagnostics { get; }

    /// <summary>Every navigable page, in order.</summary>
    public ObservableCollection<NavigationEntry> Pages { get; }

    /// <summary>The application version shown in the shell footer.</summary>
    public static string VersionText =>
        $"v{typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";

    /// <summary>
    /// The simulation-only disclaimer, shown persistently in the shell.
    /// </summary>
    /// <remarks>
    /// Deliberately always visible rather than tucked into an About dialog. StormATC is
    /// entertainment software and must never be mistaken for an operational tool.
    /// </remarks>
    public static string DisclaimerText =>
        "Flight simulation only — not for real-world aviation, navigation, or air traffic control.";

    /// <summary>Switches to a page.</summary>
    [RelayCommand]
    private void Navigate(NavigationEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        CurrentPage = entry.ViewModel;
        CurrentPageTitle = entry.Title;
        CurrentPageDescription = entry.Description;

        // Give the page a chance to refresh data that may be stale.
        if (entry.ViewModel is IRefreshablePage refreshable)
        {
            _ = refreshable.RefreshAsync();
        }
    }

    /// <summary>Navigates by zero-based index for the shell's Ctrl+1 through Ctrl+6 shortcuts.</summary>
    public void NavigateToIndex(int index)
    {
        if (index >= 0 && index < Pages.Count)
        {
            Navigate(Pages[index]);
        }
    }
}

/// <summary>One entry in the navigation rail.</summary>
public sealed record NavigationEntry(
    string Title,
    string Description,
    string Glyph,
    string Shortcut,
    ObservableObject ViewModel);

/// <summary>
/// Implemented by pages that need to reload when the user navigates to them, so that the
/// transcript and diagnostics views are never showing stale data.
/// </summary>
public interface IRefreshablePage
{
    /// <summary>Reloads the page's data.</summary>
    Task RefreshAsync();
}
