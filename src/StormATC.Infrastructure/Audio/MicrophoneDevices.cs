using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace StormATC.Infrastructure.Audio;

/// <summary>
/// Windows microphone discovery shared by every speech-recognition provider.
/// </summary>
/// <remarks>
/// WinMM (used for capture) truncates device names to 31 characters while Core Audio reports the
/// complete endpoint name. The UI shows the complete names; capture maps them back to a WinMM
/// device number by prefix.
/// </remarks>
internal static class MicrophoneDevices
{
    /// <summary>Recording devices, preferring complete Core Audio names.</summary>
    public static IReadOnlyList<string> ListInputDeviceNames(ILogger logger)
    {
        try
        {
            var legacyNames = new List<string>(WaveInEvent.DeviceCount);
            for (int index = 0; index < WaveInEvent.DeviceCount; index++)
            {
                string name = WaveInEvent.GetCapabilities(index).ProductName;
                if (!legacyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    legacyNames.Add(name);
                }
            }

            var fullNames = new List<string>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(
                    DataFlow.Capture,
                    DeviceState.Active))
                {
                    string name = endpoint.FriendlyName?.Trim() ?? string.Empty;
                    if (name.Length > 0
                        && !fullNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        fullNames.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                // WinMM remains a valid capture fallback. The UI will show its shorter
                // product names if Core Audio endpoint discovery is unavailable.
                logger.LogDebug(
                    ex,
                    "Could not resolve full Windows microphone endpoint names.");
            }

            return PreferFullInputDeviceNames(legacyNames, fullNames);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not enumerate microphone input devices.");
            return [];
        }
    }

    /// <summary>
    /// The WinMM device number for a configured name, or the Windows default communications
    /// microphone when none is configured.
    /// </summary>
    public static int ResolveInputDevice(string? configuredName, ILogger logger)
    {
        if (WaveInEvent.DeviceCount == 0)
        {
            throw new InvalidOperationException("Windows reports no microphone input devices.");
        }

        string? wanted = string.IsNullOrWhiteSpace(configuredName)
            ? TryGetWindowsDefaultInputName(logger)
            : configuredName.Trim();

        if (wanted is { Length: > 0 })
        {
            string comparableWanted = ComparableDeviceName(wanted);
            for (int index = 0; index < WaveInEvent.DeviceCount; index++)
            {
                string productName = WaveInEvent.GetCapabilities(index).ProductName;
                string comparableProduct = ComparableDeviceName(productName);

                // WinMM truncates names to 31 characters, while Core Audio returns the complete
                // name. Prefix matching maps the two views of the same endpoint.
                if (DeviceNamesReferToSameEndpoint(comparableProduct, comparableWanted))
                {
                    return index;
                }
            }

            logger.LogWarning(
                "Input device '{Device}' was not found. Using recording device 0.",
                wanted);
        }

        return 0;
    }

    /// <summary>
    /// Replaces WinMM's 31-character product labels with the corresponding complete Core Audio
    /// endpoint names while retaining any legacy-only recording devices.
    /// </summary>
    public static IReadOnlyList<string> PreferFullInputDeviceNames(
        IReadOnlyList<string> legacyNames,
        IReadOnlyList<string> fullEndpointNames)
    {
        var result = new List<string>();
        var matchedLegacyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string fullName in fullEndpointNames.Where(name =>
            !string.IsNullOrWhiteSpace(name)))
        {
            string comparableFullName = ComparableDeviceName(fullName);
            string? matchingLegacyName = legacyNames.FirstOrDefault(legacyName =>
                DeviceNamesReferToSameEndpoint(
                    ComparableDeviceName(legacyName),
                    comparableFullName));

            if (matchingLegacyName is not null
                && !result.Contains(fullName, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(fullName.Trim());
                matchedLegacyNames.Add(matchingLegacyName);
            }
        }

        foreach (string legacyName in legacyNames.Where(name =>
            !string.IsNullOrWhiteSpace(name)))
        {
            if (!matchedLegacyNames.Contains(legacyName)
                && !result.Contains(legacyName, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(legacyName.Trim());
            }
        }

        return result;
    }

    /// <summary>Normalised peak level of a 16-bit PCM buffer, 0 to 1.</summary>
    public static double PeakLevel(byte[] buffer, int bytesRecorded)
    {
        short peak = 0;
        for (int index = 0; index + 1 < bytesRecorded; index += 2)
        {
            short sample = BitConverter.ToInt16(buffer, index);
            short magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak / (double)short.MaxValue;
    }

    private static string? TryGetWindowsDefaultInputName(ILogger logger)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator
                .GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                .FriendlyName;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Could not resolve the Windows default communications microphone.");
            return null;
        }
    }

    private static string ComparableDeviceName(string value) =>
        new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static bool DeviceNamesReferToSameEndpoint(
        string firstComparableName,
        string secondComparableName) =>
        firstComparableName.Length > 0
        && secondComparableName.Length > 0
        && (firstComparableName.Equals(secondComparableName, StringComparison.Ordinal)
            || firstComparableName.StartsWith(secondComparableName, StringComparison.Ordinal)
            || secondComparableName.StartsWith(firstComparableName, StringComparison.Ordinal));
}
