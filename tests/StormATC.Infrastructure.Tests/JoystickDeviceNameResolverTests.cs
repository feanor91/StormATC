using StormATC.Infrastructure.Input;

namespace StormATC.Infrastructure.Tests;

/// <summary>Friendly controller names shown in the hardware PTT picker.</summary>
public class JoystickDeviceNameResolverTests
{
    [Fact]
    public void The_vid_and_pid_resolve_to_the_windows_oem_product_name()
    {
        string name = JoystickDeviceNameResolver.Resolve(
            manufacturerId: 0x044F,
            productId: 0xB108,
            winMmRegistryKey: "DINPUT.DLL",
            winMmProductName: "Driver for MS PC joystick",
            key => key == "VID_044F&PID_B108" ? "T.Flight Hotas X" : null);

        Assert.Equal("T.Flight Hotas X", name);
    }

    [Fact]
    public void A_real_winmm_product_name_is_preserved_when_no_oem_entry_exists()
    {
        string name = JoystickDeviceNameResolver.Resolve(
            manufacturerId: 0x1234,
            productId: 0x5678,
            winMmRegistryKey: "DINPUT.DLL",
            winMmProductName: "Example Flight Yoke",
            _ => null);

        Assert.Equal("Example Flight Yoke", name);
    }

    [Fact]
    public void A_generic_driver_name_falls_back_to_a_unique_hardware_identifier()
    {
        string name = JoystickDeviceNameResolver.Resolve(
            manufacturerId: 0x1234,
            productId: 0x5678,
            winMmRegistryKey: "DINPUT.DLL",
            winMmProductName: "Driver for MS PC joystick",
            _ => null);

        Assert.Equal("Game controller (VID_1234 PID_5678)", name);
    }

    [Fact]
    public void A_custom_winmm_registry_key_can_supply_the_oem_name()
    {
        string name = JoystickDeviceNameResolver.Resolve(
            manufacturerId: 0,
            productId: 0,
            winMmRegistryKey: "MY_CUSTOM_STICK",
            winMmProductName: "Joystick",
            key => key == "MY_CUSTOM_STICK" ? "Custom Flight Stick" : null);

        Assert.Equal("Custom Flight Stick", name);
    }
}
