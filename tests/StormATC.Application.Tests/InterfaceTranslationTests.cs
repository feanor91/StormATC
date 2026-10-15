using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using StormATC.Application.Localization;

namespace StormATC.Application.Tests;

/// <summary>
/// The French interface: every fixed string the views show has a translation, and every
/// translation keeps the values of its English original.
/// </summary>
/// <remarks>
/// These tests read the source tree, so a string added to a view or wrapped in
/// <c>UiText.T("…")</c> without a French entry fails here instead of silently showing English.
/// They never switch the process-wide interface language, so they cannot disturb tests that
/// assert English text.
/// </remarks>
public partial class InterfaceTranslationTests
{
    private static readonly string[] TranslatedAttributes = ["Text", "Content", "Header", "ToolTip", "Title"];

    [Fact]
    public void Every_fixed_string_in_the_views_has_a_French_translation()
    {
        IReadOnlyDictionary<string, string> french = UiText.FrenchTable;

        string[] missing = XamlStrings()
            .Where(text => !french.ContainsKey(text))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Add these to src/StormATC.Application/Localization/fr.json:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Every_wrapped_code_string_has_a_French_translation()
    {
        IReadOnlyDictionary<string, string> french = UiText.FrenchTable;

        string[] missing = WrappedCodeStrings()
            .Where(text => !french.ContainsKey(text))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Add these to src/StormATC.Application/Localization/fr.json:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Translations_keep_every_value_of_the_English_text()
    {
        foreach ((string english, string translated) in UiText.FrenchTable)
        {
            Assert.False(string.IsNullOrWhiteSpace(translated), $"Empty translation for \"{english}\".");
            Assert.Equal(Placeholders(english), Placeholders(translated));
        }
    }

    [Fact]
    public void An_interpolated_string_is_looked_up_by_its_composite_format()
    {
        string frequency = "118.700";
        double distance = 12.5;

        UiTextFormat plain = $"COM1 tuned to {frequency}.";
        UiTextFormat formatted = $"{distance:F0} NM, {{literal}}";

        Assert.Equal("COM1 tuned to {0}.", plain.Key);
        Assert.Equal("{0:F0} NM, {{literal}}", formatted.Key);
        Assert.True(UiText.FrenchTable.ContainsKey("COM1 tuned to {0}."));
    }

    [Theory]
    [InlineData("fr", "en-US", UiLanguage.French)]
    [InlineData("en", "fr-FR", UiLanguage.English)]
    [InlineData("", "fr-FR", UiLanguage.French)]
    [InlineData("auto", "fr-CA", UiLanguage.French)]
    [InlineData("", "de-DE", UiLanguage.English)]
    [InlineData(null, "en-GB", UiLanguage.English)]
    public void The_interface_language_follows_the_setting_or_Windows(
        string? configured,
        string windowsCulture,
        UiLanguage expected) =>
        Assert.Equal(expected, UiLanguages.Resolve(configured, CultureInfo.GetCultureInfo(windowsCulture)));

    // =====================================================================================

    /// <summary>
    /// The repository root: the folder holding <c>StormATC.sln</c> above the test binaries. A CI
    /// build maps source paths (<c>ContinuousIntegrationBuild</c>), so the compile-time path of
    /// this file is only a fallback for runs from elsewhere.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StormATC.sln")))
            {
                return directory.FullName;
            }
        }

        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
    }

    private static IEnumerable<string> SourceFiles(string relativeDirectory, string pattern) =>
        Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), relativeDirectory), pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>Literal text the XAML translator will look up, exactly as WPF parses it.</summary>
    private static IEnumerable<string> XamlStrings()
    {
        foreach (string path in SourceFiles(Path.Combine("src", "StormATC.Desktop"), "*.xaml"))
        {
            XDocument document = XDocument.Load(path);
            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    string name = attribute.Name.LocalName;
                    string value = attribute.Value;

                    if (TranslatedAttributes.Contains(name) && XamlLiteral(value) is { } literal && HasWords(literal))
                    {
                        yield return literal;
                    }

                    if (value.StartsWith('{') && StringFormat(value) is { } format && HasWords(format))
                    {
                        yield return format;
                    }
                }
            }
        }
    }

    private static string? XamlLiteral(string value) =>
        value.StartsWith("{}", StringComparison.Ordinal) ? value[2..]
        : value.StartsWith('{') ? null
        : value;

    private static string? StringFormat(string binding)
    {
        int start = binding.IndexOf("StringFormat=", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        int index = start + "StringFormat=".Length;
        string format;
        if (index < binding.Length && binding[index] == '\'')
        {
            int end = binding.IndexOf('\'', index + 1);
            format = binding[(index + 1)..end];
        }
        else
        {
            int depth = 0;
            int end = index;
            for (; end < binding.Length; end++)
            {
                char c = binding[end];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    break;
                }
            }

            format = binding[index..end];
        }

        format = format.Trim();
        return format.StartsWith("{}", StringComparison.Ordinal) ? format[2..] : format;
    }

    /// <summary>
    /// The keys of <c>UiText.T(...)</c> calls whose argument is a single literal on one line —
    /// the common case. Multi-line sentences are covered by the extraction script.
    /// </summary>
    private static IEnumerable<string> WrappedCodeStrings()
    {
        foreach (string path in SourceFiles("src", "*.cs"))
        {
            string source = File.ReadAllText(path);
            foreach (Match match in WrappedLiteral().Matches(source))
            {
                bool interpolated = match.Groups["dollar"].Success;
                string body = match.Groups["body"].Value;
                string key = interpolated ? CompositeFormat(body) : Unescape(body);
                if (HasWords(key))
                {
                    yield return key;
                }
            }
        }
    }

    /// <summary>The composite format the interpolated-string handler builds at run time.</summary>
    private static string CompositeFormat(string body)
    {
        var key = new System.Text.StringBuilder();
        int hole = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '{' && i + 1 < body.Length && body[i + 1] == '{')
            {
                key.Append("{{");
                i++;
            }
            else if (c == '}' && i + 1 < body.Length && body[i + 1] == '}')
            {
                key.Append("}}");
                i++;
            }
            else if (c == '{')
            {
                int depth = 1;
                int end = i + 1;
                while (end < body.Length && depth > 0)
                {
                    if (body[end] == '{')
                    {
                        depth++;
                    }
                    else if (body[end] == '}')
                    {
                        depth--;
                    }

                    if (depth > 0)
                    {
                        end++;
                    }
                }

                string expression = body[(i + 1)..end];
                int colon = TopLevelIndexOf(expression, ':');
                key.Append('{').Append(hole.ToString(CultureInfo.InvariantCulture));
                if (colon >= 0)
                {
                    key.Append(':').Append(expression[(colon + 1)..]);
                }

                key.Append('}');
                hole++;
                i = end;
            }
            else if (c == '\\' && i + 1 < body.Length)
            {
                key.Append(Unescape(body.Substring(i, 2)));
                i++;
            }
            else
            {
                key.Append(c);
            }
        }

        string result = key.ToString();
        return hole == 0 ? result.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal) : result;
    }

    private static int TopLevelIndexOf(string expression, char target)
    {
        int depth = 0;
        bool inString = false;
        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;
            }
            else if (c == target && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Unescape(string text) => Regex.Unescape(text);

    private static bool HasWords(string text) => LettersRegex().IsMatch(text);

    private static string[] Placeholders(string text) =>
        PlaceholderRegex().Matches(text)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

    // UiText.T("...") or UiText.T($"...") with the closing parenthesis on the same line. Nested
    // quotes inside interpolation holes are not supported by this pattern and are skipped.
    [GeneratedRegex("""UiText\.T\((?<dollar>\$)?"(?<body>(?:[^"\\\r\n]|\\.)*)"\)""")]
    private static partial Regex WrappedLiteral();

    [GeneratedRegex("[A-Za-z]{2}")]
    private static partial Regex LettersRegex();

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})")]
    private static partial Regex PlaceholderRegex();
}
