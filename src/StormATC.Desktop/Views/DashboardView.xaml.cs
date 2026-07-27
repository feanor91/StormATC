using System.Windows.Controls;
using System.Windows.Input;
using StormATC.Desktop.ViewModels;

namespace StormATC.Desktop.Views;

/// <summary>The dashboard page.</summary>
/// <remarks>
/// The code-behind handles only the interactions WPF cannot express as a command: press-and-hold
/// push-to-talk, and Enter in the developer transmission box.
/// </remarks>
public partial class DashboardView : UserControl
{
    /// <summary>Creates the dashboard page.</summary>
    public DashboardView() => InitializeComponent();

    private DashboardViewModel? ViewModel => DataContext as DashboardViewModel;

    /// <summary>Starts transmitting on mouse-down rather than WPF's mouse-up Click event.</summary>
    private void OnPushToTalkPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button)
        {
            button.CaptureMouse();
        }

        if (ViewModel is DashboardViewModel viewModel
            && viewModel.StartTransmissionCommand.CanExecute(null))
        {
            viewModel.StartTransmissionCommand.Execute(null);
        }
    }

    /// <summary>Ends the transmission when the held push-to-talk button is released.</summary>
    private void OnPushToTalkReleased(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button && button.IsMouseCaptured)
        {
            button.ReleaseMouseCapture();
        }

        if (ViewModel is DashboardViewModel viewModel
            && viewModel.StopTransmissionCommand.CanExecute(null))
        {
            viewModel.StopTransmissionCommand.Execute(null);
        }
    }

    /// <summary>Sends the typed transmission on Enter.</summary>
    private void OnManualTransmissionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not DashboardViewModel viewModel)
        {
            return;
        }

        e.Handled = true;

        if (viewModel.SendManualTransmissionCommand.CanExecute(null))
        {
            viewModel.SendManualTransmissionCommand.Execute(null);
        }
    }
}
