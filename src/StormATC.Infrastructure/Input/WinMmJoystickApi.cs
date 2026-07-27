using System.Runtime.InteropServices;
using StormATC.Application.Abstractions;

namespace StormATC.Infrastructure.Input;

/// <summary>The small native boundary used by the polling service and replaced by tests.</summary>
internal interface IJoystickApi
{
    IReadOnlyList<JoystickDevice> GetConnectedDevices();

    bool TryGetButtons(int deviceId, out uint buttons);
}

/// <summary>
/// Reads standard USB joysticks, yokes, and HOTAS controllers through Windows winmm.
/// </summary>
/// <remarks>
/// winmm is included with Windows. It avoids an external driver or a DirectInput dependency and
/// supports the first 32 buttons, which is the limit of the API's button bitmask.
/// </remarks>
internal sealed class WinMmJoystickApi : IJoystickApi
{
    private const uint NoError = 0;
    private const uint ReturnButtons = 0x00000080;
    private const int MaximumSupportedDevices = 32;

    public IReadOnlyList<JoystickDevice> GetConnectedDevices()
    {
        int availableSlots = (int)Math.Min(JoyGetNumDevs(), MaximumSupportedDevices);
        var devices = new List<JoystickDevice>();

        for (int id = 0; id < availableSlots; id++)
        {
            var caps = new JoyCaps();

            if (JoyGetDevCaps((nuint)id, ref caps, (uint)Marshal.SizeOf<JoyCaps>()) != NoError
                || !TryGetButtons(id, out _))
            {
                continue;
            }

            string name = JoystickDeviceNameResolver.Resolve(
                caps.ManufacturerId,
                caps.ProductId,
                caps.RegistryKey,
                caps.ProductName);

            devices.Add(
                new JoystickDevice(
                    id,
                    name,
                    (int)Math.Clamp(caps.NumberOfButtons, 0u, 32u)));
        }

        return devices;
    }

    public bool TryGetButtons(int deviceId, out uint buttons)
    {
        var state = new JoyInfoEx
        {
            Size = (uint)Marshal.SizeOf<JoyInfoEx>(),
            Flags = ReturnButtons,
        };

        uint result = JoyGetPosEx((uint)deviceId, ref state);
        buttons = result == NoError ? state.Buttons : 0;
        return result == NoError;
    }

    [DllImport("winmm.dll", EntryPoint = "joyGetNumDevs")]
    private static extern uint JoyGetNumDevs();

    [DllImport(
        "winmm.dll",
        EntryPoint = "joyGetDevCapsW",
        CharSet = CharSet.Unicode)]
    private static extern uint JoyGetDevCaps(
        nuint joystickId,
        ref JoyCaps capabilities,
        uint capabilitiesSize);

    [DllImport("winmm.dll", EntryPoint = "joyGetPosEx")]
    private static extern uint JoyGetPosEx(uint joystickId, ref JoyInfoEx state);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;

        public uint XMinimum;
        public uint XMaximum;
        public uint YMinimum;
        public uint YMaximum;
        public uint ZMinimum;
        public uint ZMaximum;
        public uint NumberOfButtons;
        public uint MinimumPollingPeriod;
        public uint MaximumPollingPeriod;
        public uint RMinimum;
        public uint RMaximum;
        public uint UMinimum;
        public uint UMaximum;
        public uint VMinimum;
        public uint VMaximum;
        public uint Capabilities;
        public uint MaximumAxes;
        public uint NumberOfAxes;
        public uint MaximumButtons;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string RegistryKey;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string OemDriver;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public uint Size;
        public uint Flags;
        public uint X;
        public uint Y;
        public uint Z;
        public uint R;
        public uint U;
        public uint V;
        public uint Buttons;
        public uint ButtonNumber;
        public uint PointOfView;
        public uint Reserved1;
        public uint Reserved2;
    }
}
