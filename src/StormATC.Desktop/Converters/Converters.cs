using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using StormATC.Domain;

namespace StormATC.Desktop.Converters;

/// <summary>Collapses an element when a bound boolean is false.</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>Collapses an element when a bound boolean is true.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}

/// <summary>Inverts a boolean, for enabling a control when a flag is false.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>Collapses an element when a bound string is empty.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a provider state to a status colour.
/// </summary>
/// <remarks>
/// Colour is always paired with a text label in the UI. Nothing in StormATC communicates state
/// by colour alone, so the interface stays readable to colour-blind users.
/// </remarks>
public sealed class ProviderStateToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            ProviderConnectionState.Connected => "StatusOkBrush",
            ProviderConnectionState.Configured => "StatusOkBrush",
            ProviderConnectionState.Connecting => "StatusWarnBrush",
            ProviderConnectionState.Degraded => "StatusWarnBrush",
            ProviderConnectionState.Error => "StatusErrorBrush",
            ProviderConnectionState.Unavailable => "StatusErrorBrush",
            _ => "StatusIdleBrush",
        };

        return LookupBrush(key);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static Brush LookupBrush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>Maps the push-to-talk state to a colour.</summary>
public sealed class SpeechStateToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            SpeechState.Listening => "StatusOkBrush",
            SpeechState.Connecting => "StatusWarnBrush",
            SpeechState.Processing => "StatusWarnBrush",
            SpeechState.ControllerResponding => "AccentBrush",
            SpeechState.PlayingAudio => "AccentBrush",
            SpeechState.Error => "StatusErrorBrush",
            _ => "StatusIdleBrush",
        };

        return ProviderStateToBrushConverter.LookupBrush(key);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a conversation speaker flag to the pilot or controller accent colour.</summary>
public sealed class SpeakerToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ProviderStateToBrushConverter.LookupBrush(
            value is true ? "PilotAccentBrush" : "ControllerAccentBrush");

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True when two bound values are the same instance. Used to mark the selected navigation item.
/// </summary>
public sealed class ReferenceEqualityConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is { Length: 2 } && ReferenceEquals(values[0], values[1]);

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Scales a normalised 0-1 audio level into a pixel width for the input meter.
/// </summary>
public sealed class LevelToWidthConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double level = value is double d ? Math.Clamp(d, 0, 1) : 0;
        double maximum = parameter is string text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : 240;

        return level * maximum;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
