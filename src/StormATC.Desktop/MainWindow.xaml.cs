using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Desktop.ViewModels;

namespace StormATC.Desktop;

/// <summary>
/// The application shell.
/// </summary>
/// <remarks>
/// Holds the push-to-talk key handling, which has to live in the window because a global key
/// binding is a window concern, not a view model one. Everything it does is delegated
/// immediately to the dashboard view model.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly IJoystickInputService _joystickInput;
    private readonly IOptionsMonitor<AudioOptions> _audioOptions;
    private bool _keyboardPushToTalkHeld;
    private bool _joystickPushToTalkHeld;

    /// <summary>Creates the shell.</summary>
    public MainWindow(
        IJoystickInputService joystickInput,
        IOptionsMonitor<AudioOptions> audioOptions)
    {
        _joystickInput = joystickInput ?? throw new ArgumentNullException(nameof(joystickInput));
        _audioOptions = audioOptions ?? throw new ArgumentNullException(nameof(audioOptions));

        InitializeComponent();

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Closed += OnClosed;
        _joystickInput.PushToTalkPressed += OnJoystickPushToTalkPressed;
        _joystickInput.PushToTalkReleased += OnJoystickPushToTalkReleased;
    }

    private DashboardViewModel? Dashboard => (DataContext as MainWindowViewModel)?.Dashboard;

    /// <summary>
    /// Starts a transmission while the push-to-talk key is held.
    /// </summary>
    /// <remarks>
    /// Ignored while a text box has focus: typing a clearance into the developer console must
    /// not open the microphone on every space character.
    /// </remarks>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleNavigationShortcut(e))
        {
            return;
        }

        if (e.Key != GetPushToTalkKey()
            || _keyboardPushToTalkHeld
            || IsTextInputFocused())
        {
            return;
        }

        _keyboardPushToTalkHeld = true;
        e.Handled = true;
        StartTransmissionIfNeeded();
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != GetPushToTalkKey() || !_keyboardPushToTalkHeld)
        {
            return;
        }

        _keyboardPushToTalkHeld = false;
        e.Handled = true;
        StopTransmissionIfReleased();
    }

    private void OnJoystickPushToTalkPressed(
        object? sender,
        JoystickButtonEventArgs e) =>
        RunOnUiThread(() =>
        {
            if (_joystickPushToTalkHeld)
            {
                return;
            }

            _joystickPushToTalkHeld = true;
            StartTransmissionIfNeeded();
        });

    private void OnJoystickPushToTalkReleased(
        object? sender,
        JoystickButtonEventArgs e) =>
        RunOnUiThread(() =>
        {
            if (!_joystickPushToTalkHeld)
            {
                return;
            }

            _joystickPushToTalkHeld = false;
            StopTransmissionIfReleased();
        });

    private void StartTransmissionIfNeeded()
    {
        if (Dashboard is DashboardViewModel dashboard
            && dashboard.StartTransmissionCommand.CanExecute(null))
        {
            dashboard.StartTransmissionCommand.Execute(null);
        }
    }

    private void StopTransmissionIfReleased()
    {
        if (_keyboardPushToTalkHeld || _joystickPushToTalkHeld)
        {
            return;
        }

        if (Dashboard is DashboardViewModel dashboard
            && dashboard.StopTransmissionCommand.CanExecute(null))
        {
            dashboard.StopTransmissionCommand.Execute(null);
        }
    }

    private void RunOnUiThread(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.BeginInvoke(action);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _joystickInput.PushToTalkPressed -= OnJoystickPushToTalkPressed;
        _joystickInput.PushToTalkReleased -= OnJoystickPushToTalkReleased;
    }

    private static bool IsTextInputFocused() =>
        Keyboard.FocusedElement is System.Windows.Controls.TextBox
            or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.ComboBox;

    private bool TryHandleNavigationShortcut(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0
            || DataContext is not MainWindowViewModel viewModel)
        {
            return false;
        }

        int index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 or Key.OemComma => 4,
            Key.D6 or Key.NumPad6 => 5,
            _ => -1,
        };

        if (index < 0)
        {
            return false;
        }

        viewModel.NavigateToIndex(index);
        e.Handled = true;
        return true;
    }

    private Key GetPushToTalkKey()
    {
        string configured = _audioOptions.CurrentValue.PushToTalkKey.Trim();

        if (configured.Equals("Spacebar", StringComparison.OrdinalIgnoreCase))
        {
            return Key.Space;
        }

        return Enum.TryParse(configured, ignoreCase: true, out Key key)
            ? key
            : Key.Space;
    }
}
