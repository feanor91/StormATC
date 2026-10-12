using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace StormATC.Application.Localization;

/// <summary>The language of StormATC's interface (menus, labels, messages).</summary>
/// <remarks>
/// Independent of the radio language: a French pilot can fly with English ATC and a French
/// interface, or the reverse.
/// </remarks>
public enum UiLanguage
{
    /// <summary>English, the language the interface is written in.</summary>
    English = 0,

    /// <summary>French.</summary>
    French,
}

/// <summary>Conversions between <see cref="UiLanguage"/> and configuration values.</summary>
public static class UiLanguages
{
    /// <summary>
    /// Resolves a configuration value: <c>en</c>, <c>fr</c>, or empty/<c>auto</c> to follow the
    /// Windows display language.
    /// </summary>
    public static UiLanguage Resolve(string? configured, CultureInfo? systemCulture = null)
    {
        string value = configured?.Trim() ?? string.Empty;

        if (value.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
        {
            return UiLanguage.French;
        }

        if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return UiLanguage.English;
        }

        CultureInfo culture = systemCulture ?? CultureInfo.InstalledUICulture;
        return string.Equals(culture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.French
            : UiLanguage.English;
    }
}

/// <summary>
/// Translates interface text. The English text is the key, so untranslated text simply stays in
/// English and the source code reads naturally.
/// </summary>
/// <remarks>
/// <para>
/// Plain text: <c>UiText.T("Save settings")</c>. Text with values uses an interpolated string,
/// which is looked up as a composite format: <c>UiText.T($"COM1 tuned to {frequency}.")</c> is
/// translated through the key <c>"COM1 tuned to {0}."</c>, then formatted.
/// </para>
/// <para>
/// The language is chosen once at startup; translations live in the embedded
/// <c>Localization/fr.json</c>. A test checks that every interface string has a translation.
/// </para>
/// </remarks>
public static class UiText
{
    private const string FrenchResourceName = "StormATC.Application.Localization.fr.json";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> French =
        new(() => LoadTable(FrenchResourceName));

    private static volatile IReadOnlyDictionary<string, string> _active =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The interface language in use.</summary>
    public static UiLanguage Language { get; private set; } = UiLanguage.English;

    /// <summary>The French table, keyed by English text.</summary>
    public static IReadOnlyDictionary<string, string> FrenchTable => French.Value;

    /// <summary>Selects the interface language. Call once, before any window is created.</summary>
    public static void SetLanguage(UiLanguage language)
    {
        Language = language;
        _active = language == UiLanguage.French
            ? French.Value
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>The translation of <paramref name="english"/>, or the English text itself.</summary>
    public static string T(string english)
    {
        ArgumentNullException.ThrowIfNull(english);
        return Lookup(english);
    }

    /// <summary>Translates and formats an interpolated string. See the class remarks.</summary>
    public static string T(ref UiTextFormat text) => text.ToStringAndClear();

    /// <summary>True when the active language has a translation for <paramref name="english"/>.</summary>
    public static bool HasTranslation(string english) =>
        _active.ContainsKey(english);

    internal static string Lookup(string key) =>
        _active.TryGetValue(key, out string? translated) && translated.Length > 0
            ? translated
            : key;

    private static Dictionary<string, string> LoadTable(string resourceName)
    {
        using Stream? stream = typeof(UiText).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string>? table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        return table is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(table, StringComparer.Ordinal);
    }
}

/// <summary>
/// Builds the composite-format key of an interpolated interface string, then translates and
/// formats it with the current culture.
/// </summary>
[InterpolatedStringHandler]
public struct UiTextFormat
{
    private StringBuilder? _format;
    private List<object?>? _arguments;

    /// <summary>Called by the compiler.</summary>
    public UiTextFormat(int literalLength, int formattedCount)
    {
        _format = new StringBuilder(literalLength + (formattedCount * 4));
        _arguments = new List<object?>(formattedCount);
    }

    /// <summary>Called by the compiler for literal text.</summary>
    public readonly void AppendLiteral(string value) =>
        _format!.Append(value.Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal));

    /// <summary>Called by the compiler for a hole.</summary>
    public readonly void AppendFormatted<T>(T value) => AppendHole(value, alignment: null, format: null);

    /// <summary>Called by the compiler for a hole with a format.</summary>
    public readonly void AppendFormatted<T>(T value, string? format) =>
        AppendHole(value, alignment: null, format);

    /// <summary>Called by the compiler for a hole with an alignment.</summary>
    public readonly void AppendFormatted<T>(T value, int alignment) =>
        AppendHole(value, alignment, format: null);

    /// <summary>Called by the compiler for a hole with an alignment and a format.</summary>
    public readonly void AppendFormatted<T>(T value, int alignment, string? format) =>
        AppendHole(value, alignment, format);

    /// <summary>The composite format, for example <c>"COM1 tuned to {0}."</c>.</summary>
    public readonly string Key => _format?.ToString() ?? string.Empty;

    /// <summary>Translates, formats and returns the text.</summary>
    public string ToStringAndClear()
    {
        string key = Key;
        object?[] arguments = _arguments?.ToArray() ?? [];
        _format = null;
        _arguments = null;

        string translated = UiText.Lookup(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, translated, arguments);
        }
        catch (FormatException)
        {
            // A broken translation must never take the interface down: fall back to English.
            return string.Format(CultureInfo.CurrentCulture, key, arguments);
        }
    }

    private readonly void AppendHole(object? value, int? alignment, string? format)
    {
        _format!.Append('{').Append(_arguments!.Count.ToString(CultureInfo.InvariantCulture));
        if (alignment is int width)
        {
            _format.Append(',').Append(width.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(format))
        {
            _format.Append(':').Append(format);
        }

        _format.Append('}');
        _arguments.Add(value);
    }
}
