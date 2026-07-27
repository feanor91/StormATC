using System.Numerics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;

namespace StormATC.Infrastructure.Input;

/// <summary>
/// Polls a configured Windows joystick button and emits reliable press/release transitions.
/// </summary>
/// <remarks>
/// The service performs no simulator writes and does not use the MSFS SDK. A disconnected
/// controller is treated as a release so microphone capture can never remain stuck open.
/// </remarks>
public sealed class WindowsJoystickInputService :
    BackgroundService,
    IJoystickInputService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(1);

    private readonly IJoystickApi _api;
    private readonly ILogger<WindowsJoystickInputService> _logger;
    private readonly object _sync = new();

    private IReadOnlyList<JoystickDevice> _devices = [];
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private JoystickButtonBinding? _binding;
    private bool _enabled;
    private bool _boundButtonPressed;
    private CaptureRequest? _captureRequest;

    /// <summary>Creates the Windows joystick polling service from application settings.</summary>
    public WindowsJoystickInputService(
        IOptions<AudioOptions> options,
        ILogger<WindowsJoystickInputService> logger)
        : this(
            new WinMmJoystickApi(),
            options?.Value ?? throw new ArgumentNullException(nameof(options)),
            logger)
    {
    }

    internal WindowsJoystickInputService(
        IJoystickApi api,
        AudioOptions options,
        ILogger<WindowsJoystickInputService> logger)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);

        if (options.JoystickDeviceId is int deviceId
            && !string.IsNullOrWhiteSpace(options.JoystickDeviceName)
            && options.JoystickButtonIndex is int buttonIndex)
        {
            _binding = new JoystickButtonBinding(
                deviceId,
                options.JoystickDeviceName.Trim(),
                buttonIndex);
        }

        _enabled = options.JoystickPushToTalkEnabled && _binding is not null;
    }

    /// <inheritdoc />
    public event EventHandler<JoystickButtonEventArgs>? PushToTalkPressed;

    /// <inheritdoc />
    public event EventHandler<JoystickButtonEventArgs>? PushToTalkReleased;

    /// <inheritdoc />
    public JoystickButtonBinding? CurrentBinding
    {
        get
        {
            lock (_sync)
            {
                return _binding;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<JoystickDevice> GetConnectedDevices()
    {
        IReadOnlyList<JoystickDevice> devices = _api.GetConnectedDevices();

        lock (_sync)
        {
            _devices = devices;
            _lastDiscovery = DateTimeOffset.UtcNow;
        }

        return devices;
    }

    /// <inheritdoc />
    public void Configure(JoystickButtonBinding? binding, bool enabled)
    {
        JoystickButtonBinding? releasedBinding = null;

        lock (_sync)
        {
            if (_boundButtonPressed)
            {
                releasedBinding = _binding;
                _boundButtonPressed = false;
            }

            _binding = binding;
            _enabled = enabled && binding is not null;
            _lastDiscovery = DateTimeOffset.MinValue;
        }

        if (releasedBinding is not null)
        {
            RaiseSafely(PushToTalkReleased, releasedBinding, "release");
        }
    }

    /// <inheritdoc />
    public async Task<JoystickButtonBinding> CaptureNextButtonAsync(
        int deviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        JoystickDevice device = GetConnectedDevices()
            .FirstOrDefault(candidate => candidate.Id == deviceId)
            ?? throw new InvalidOperationException("The selected joystick is no longer connected.");

        if (!_api.TryGetButtons(deviceId, out uint baselineButtons))
        {
            throw new InvalidOperationException("The selected joystick could not be read.");
        }

        var request = new CaptureRequest(
            device,
            baselineButtons,
            new TaskCompletionSource<JoystickButtonBinding>(
                TaskCreationOptions.RunContinuationsAsynchronously));

        JoystickButtonBinding? releasedBinding = null;
        lock (_sync)
        {
            if (_captureRequest is not null)
            {
                throw new InvalidOperationException("A joystick button capture is already active.");
            }

            if (_boundButtonPressed)
            {
                releasedBinding = _binding;
                _boundButtonPressed = false;
            }

            _captureRequest = request;
        }

        if (releasedBinding is not null)
        {
            RaiseSafely(PushToTalkReleased, releasedBinding, "release");
        }

        try
        {
            return await request.Completion.Task
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_captureRequest, request))
                {
                    _captureRequest = null;
                }
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        try
        {
            while (await timer
                .WaitForNextTickAsync(stoppingToken)
                .ConfigureAwait(false))
            {
                PollOnce();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            ReleaseBoundButton();
        }
    }

    private void PollOnce()
    {
        CaptureRequest? capture;
        lock (_sync)
        {
            capture = _captureRequest;
        }

        if (capture is not null)
        {
            PollCapture(capture);
            return;
        }

        PollConfiguredBinding();
    }

    private void PollCapture(CaptureRequest capture)
    {
        if (!_api.TryGetButtons(capture.Device.Id, out uint buttons))
        {
            return;
        }

        uint newlyPressed;
        lock (_sync)
        {
            if (!ReferenceEquals(_captureRequest, capture))
            {
                return;
            }

            newlyPressed = buttons & ~capture.PreviousButtons;
            capture.PreviousButtons = buttons;
        }

        if (newlyPressed == 0)
        {
            return;
        }

        int buttonIndex = BitOperations.TrailingZeroCount(newlyPressed);
        if (buttonIndex >= capture.Device.ButtonCount)
        {
            return;
        }

        capture.Completion.TrySetResult(
            new JoystickButtonBinding(
                capture.Device.Id,
                capture.Device.Name,
                buttonIndex));
    }

    private void PollConfiguredBinding()
    {
        JoystickButtonBinding? configured;
        bool enabled;

        lock (_sync)
        {
            configured = _binding;
            enabled = _enabled;
        }

        if (!enabled || configured is null)
        {
            ReleaseBoundButton();
            return;
        }

        JoystickDevice? device = ResolveDevice(configured);
        bool pressed = device is not null
            && configured.ButtonIndex < device.ButtonCount
            && _api.TryGetButtons(device.Id, out uint buttons)
            && (buttons & (1u << configured.ButtonIndex)) != 0;

        var effectiveBinding = device is null
            ? configured
            : configured with
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
            };

        bool raisePressed = false;
        bool raiseReleased = false;

        lock (_sync)
        {
            if (pressed && !_boundButtonPressed)
            {
                _boundButtonPressed = true;
                raisePressed = true;
            }
            else if (!pressed && _boundButtonPressed)
            {
                _boundButtonPressed = false;
                raiseReleased = true;
            }
        }

        if (raisePressed)
        {
            RaiseSafely(PushToTalkPressed, effectiveBinding, "press");
        }
        else if (raiseReleased)
        {
            RaiseSafely(PushToTalkReleased, effectiveBinding, "release");
        }
    }

    private JoystickDevice? ResolveDevice(JoystickButtonBinding binding)
    {
        IReadOnlyList<JoystickDevice> devices;

        lock (_sync)
        {
            if (DateTimeOffset.UtcNow - _lastDiscovery >= DiscoveryInterval)
            {
                _devices = _api.GetConnectedDevices();
                _lastDiscovery = DateTimeOffset.UtcNow;
            }

            devices = _devices;
        }

        return devices.FirstOrDefault(
                   device => device.Id == binding.DeviceId
                       && device.Name.Equals(
                           binding.DeviceName,
                           StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(
                device => device.Name.Equals(
                    binding.DeviceName,
                    StringComparison.OrdinalIgnoreCase));
    }

    private void ReleaseBoundButton()
    {
        JoystickButtonBinding? binding = null;

        lock (_sync)
        {
            if (_boundButtonPressed)
            {
                _boundButtonPressed = false;
                binding = _binding;
            }
        }

        if (binding is not null)
        {
            RaiseSafely(PushToTalkReleased, binding, "release");
        }
    }

    private void RaiseSafely(
        EventHandler<JoystickButtonEventArgs>? handlers,
        JoystickButtonBinding binding,
        string transition)
    {
        if (handlers is null)
        {
            return;
        }

        var arguments = new JoystickButtonEventArgs(binding);

        foreach (EventHandler<JoystickButtonEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, arguments);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "A joystick push-to-talk {Transition} handler failed.",
                    transition);
            }
        }
    }

    private sealed class CaptureRequest(
        JoystickDevice device,
        uint previousButtons,
        TaskCompletionSource<JoystickButtonBinding> completion)
    {
        public JoystickDevice Device { get; } = device;

        public uint PreviousButtons { get; set; } = previousButtons;

        public TaskCompletionSource<JoystickButtonBinding> Completion { get; } = completion;
    }
}
