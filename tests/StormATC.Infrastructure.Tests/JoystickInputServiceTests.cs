using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Infrastructure.Input;

namespace StormATC.Infrastructure.Tests;

/// <summary>Hardware push-to-talk transitions without requiring a physical test controller.</summary>
public class JoystickInputServiceTests
{
    [Fact]
    public async Task The_bound_button_emits_one_press_and_one_release()
    {
        var api = new FakeJoystickApi(new JoystickDevice(2, "Test HOTAS", 12));
        using WindowsJoystickInputService service = CreateService(
            api,
            enabled: true,
            deviceId: 2,
            deviceName: "Test HOTAS",
            buttonIndex: 4);

        var pressed = new TaskCompletionSource<JoystickButtonBinding>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<JoystickButtonBinding>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.PushToTalkPressed += (_, args) => pressed.TrySetResult(args.Binding);
        service.PushToTalkReleased += (_, args) => released.TrySetResult(args.Binding);

        await service.StartAsync(CancellationToken.None);
        try
        {
            api.SetButtons(2, 1u << 4);
            JoystickButtonBinding press = await pressed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(5, press.ButtonNumber);
            Assert.False(released.Task.IsCompleted);

            api.SetButtons(2, 0);
            JoystickButtonBinding release =
                await released.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(press, release);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_button_already_held_when_binding_starts_is_ignored()
    {
        var api = new FakeJoystickApi(new JoystickDevice(1, "Yoke", 8));
        api.SetButtons(1, 1u << 2);

        using WindowsJoystickInputService service = CreateService(api);
        await service.StartAsync(CancellationToken.None);

        try
        {
            Task<JoystickButtonBinding> capture = service.CaptureNextButtonAsync(
                1,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

            await Task.Delay(100);
            Assert.False(capture.IsCompleted);

            api.SetButtons(1, 0);
            await Task.Delay(75);
            api.SetButtons(1, 1u << 2);

            JoystickButtonBinding binding = await capture;

            Assert.Equal("Yoke", binding.DeviceName);
            Assert.Equal(2, binding.ButtonIndex);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_saved_binding_follows_the_controller_when_its_Windows_id_changes()
    {
        var api = new FakeJoystickApi(new JoystickDevice(7, "Flight Stick", 16));
        using WindowsJoystickInputService service = CreateService(
            api,
            enabled: true,
            deviceId: 1,
            deviceName: "Flight Stick",
            buttonIndex: 6);

        var pressed = new TaskCompletionSource<JoystickButtonBinding>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.PushToTalkPressed += (_, args) => pressed.TrySetResult(args.Binding);

        await service.StartAsync(CancellationToken.None);
        try
        {
            api.SetButtons(7, 1u << 6);
            JoystickButtonBinding binding =
                await pressed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(7, binding.DeviceId);
            Assert.Equal("Flight Stick", binding.DeviceName);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disconnecting_a_pressed_controller_emits_release()
    {
        var api = new FakeJoystickApi(new JoystickDevice(3, "Throttle", 10));
        using WindowsJoystickInputService service = CreateService(
            api,
            enabled: true,
            deviceId: 3,
            deviceName: "Throttle",
            buttonIndex: 1);

        var pressed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.PushToTalkPressed += (_, _) => pressed.TrySetResult();
        service.PushToTalkReleased += (_, _) => released.TrySetResult();

        await service.StartAsync(CancellationToken.None);
        try
        {
            api.SetButtons(3, 1u << 1);
            await pressed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            api.Disconnect(3);
            await released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static WindowsJoystickInputService CreateService(
        FakeJoystickApi api,
        bool enabled = false,
        int? deviceId = null,
        string? deviceName = null,
        int? buttonIndex = null) =>
        new(
            api,
            new AudioOptions
            {
                JoystickPushToTalkEnabled = enabled,
                JoystickDeviceId = deviceId,
                JoystickDeviceName = deviceName,
                JoystickButtonIndex = buttonIndex,
            },
            NullLogger<WindowsJoystickInputService>.Instance);

    private sealed class FakeJoystickApi(params JoystickDevice[] devices) : IJoystickApi
    {
        private readonly object _sync = new();
        private readonly Dictionary<int, JoystickDevice> _devices =
            devices.ToDictionary(device => device.Id);
        private readonly Dictionary<int, uint> _buttons =
            devices.ToDictionary(device => device.Id, _ => 0u);

        public IReadOnlyList<JoystickDevice> GetConnectedDevices()
        {
            lock (_sync)
            {
                return [.. _devices.Values.OrderBy(device => device.Id)];
            }
        }

        public bool TryGetButtons(int deviceId, out uint buttons)
        {
            lock (_sync)
            {
                return _buttons.TryGetValue(deviceId, out buttons);
            }
        }

        public void SetButtons(int deviceId, uint buttons)
        {
            lock (_sync)
            {
                if (_devices.ContainsKey(deviceId))
                {
                    _buttons[deviceId] = buttons;
                }
            }
        }

        public void Disconnect(int deviceId)
        {
            lock (_sync)
            {
                _devices.Remove(deviceId);
                _buttons.Remove(deviceId);
            }
        }
    }
}
