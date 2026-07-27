namespace StormATC.Application.Abstractions;

/// <summary>A joystick or HOTAS exposed by the Windows multimedia input stack.</summary>
public sealed record JoystickDevice(int Id, string Name, int ButtonCount)
{
    /// <summary>A concise label suitable for a device picker.</summary>
    public string DisplayName => $"{Name} ({ButtonCount} buttons)";
}

/// <summary>One physical joystick button assigned to push-to-talk.</summary>
public sealed record JoystickButtonBinding(
    int DeviceId,
    string DeviceName,
    int ButtonIndex)
{
    /// <summary>The one-based button number shown to the pilot.</summary>
    public int ButtonNumber => ButtonIndex + 1;

    /// <summary>A concise description of the assignment.</summary>
    public string DisplayName => $"{DeviceName} · Button {ButtonNumber}";
}

/// <summary>Describes a bound joystick button transition.</summary>
public sealed class JoystickButtonEventArgs(JoystickButtonBinding binding) : EventArgs
{
    /// <summary>The button that changed state.</summary>
    public JoystickButtonBinding Binding { get; } =
        binding ?? throw new ArgumentNullException(nameof(binding));
}

/// <summary>
/// Discovers Windows joystick devices and turns one configured button into hold-to-talk events.
/// </summary>
public interface IJoystickInputService
{
    /// <summary>Raised once when the configured button changes from released to pressed.</summary>
    event EventHandler<JoystickButtonEventArgs>? PushToTalkPressed;

    /// <summary>Raised once when the configured button is released or its device disconnects.</summary>
    event EventHandler<JoystickButtonEventArgs>? PushToTalkReleased;

    /// <summary>The currently configured assignment, even when its device is disconnected.</summary>
    JoystickButtonBinding? CurrentBinding { get; }

    /// <summary>Returns a fresh snapshot of connected joystick and HOTAS devices.</summary>
    IReadOnlyList<JoystickDevice> GetConnectedDevices();

    /// <summary>Applies an assignment immediately without requiring an application restart.</summary>
    void Configure(JoystickButtonBinding? binding, bool enabled);

    /// <summary>
    /// Waits for the next newly pressed button on one device. A button held before capture starts
    /// is ignored until it is released and pressed again.
    /// </summary>
    Task<JoystickButtonBinding> CaptureNextButtonAsync(
        int deviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
