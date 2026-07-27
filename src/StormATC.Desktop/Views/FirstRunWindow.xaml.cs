using System.Windows;

namespace StormATC.Desktop.Views;

/// <summary>
/// The first-run screen: scope, cost, and what leaves the computer.
/// </summary>
/// <remarks>
/// Declining is a first-class outcome, not a dead end. StormATC is fully usable with no cloud
/// services at all, so "continue without cloud services" leads to the same application — just
/// with deterministic phraseology and typed transmissions.
/// </remarks>
public partial class FirstRunWindow : Window
{
    /// <summary>Creates the first-run screen.</summary>
    public FirstRunWindow() => InitializeComponent();

    /// <summary>True when the user accepted the cloud-services cost notice.</summary>
    public bool CloudServicesAccepted { get; private set; }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        CloudServicesAccepted = true;
        Close();
    }

    private void OnDecline(object sender, RoutedEventArgs e)
    {
        CloudServicesAccepted = false;
        Close();
    }
}
