using System.Security;
using Microsoft.Win32;

namespace StormATC.Infrastructure.Input;

/// <summary>Turns WinMM's driver label into the controller's real Windows OEM product name.</summary>
internal static class JoystickDeviceNameResolver
{
    private const string OemRegistryPath =
        @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";

    internal static string Resolve(
        ushort manufacturerId,
        ushort productId,
        string? winMmRegistryKey,
        string? winMmProductName) =>
        Resolve(
            manufacturerId,
            productId,
            winMmRegistryKey,
            winMmProductName,
            ReadOemName);

    internal static string Resolve(
        ushort manufacturerId,
        ushort productId,
        string? winMmRegistryKey,
        string? winMmProductName,
        Func<string, string?> oemNameLookup)
    {
        ArgumentNullException.ThrowIfNull(oemNameLookup);

        string hardwareKey = $"VID_{manufacturerId:X4}&PID_{productId:X4}";
        string? oemName = oemNameLookup(hardwareKey);

        if (string.IsNullOrWhiteSpace(oemName)
            && !string.IsNullOrWhiteSpace(winMmRegistryKey)
            && !winMmRegistryKey.Equals("DINPUT.DLL", StringComparison.OrdinalIgnoreCase))
        {
            oemName = oemNameLookup(winMmRegistryKey.Trim());
        }

        if (!string.IsNullOrWhiteSpace(oemName))
        {
            return oemName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(winMmProductName)
            && !LooksLikeGenericDriverName(winMmProductName))
        {
            return winMmProductName.Trim();
        }

        // A VID/PID label is still unique and actionable on unusual drivers that register no
        // OEMName. It is preferable to a localised "Driver for MS PC joystick" label that makes
        // several different controllers look identical.
        return $"Game controller ({hardwareKey.Replace('&', ' ')})";
    }

    private static bool LooksLikeGenericDriverName(string name) =>
        name.Contains("driver for", StringComparison.OrdinalIgnoreCase)
        || name.Contains("pc joystick", StringComparison.OrdinalIgnoreCase)
        || name.Contains("pc-joystick", StringComparison.OrdinalIgnoreCase)
        || name.Contains("pc-styrespak", StringComparison.OrdinalIgnoreCase)
        || name.Equals("joystick", StringComparison.OrdinalIgnoreCase);

    private static string? ReadOemName(string hardwareKey)
    {
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? deviceKey =
                        baseKey.OpenSubKey($@"{OemRegistryPath}\{hardwareKey}");

                    if (deviceKey?.GetValue("OEMName") is string name
                        && !string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
                catch (Exception ex) when (
                    ex is SecurityException
                        or UnauthorizedAccessException
                        or IOException)
                {
                    // Registry lookup is an enhancement. Device discovery still succeeds with
                    // the WinMM product name or the VID/PID fallback when a key cannot be read.
                }
            }
        }

        return null;
    }
}
