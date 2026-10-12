using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using StormATC.Application.Localization;

namespace StormATC.Desktop.Localization;

/// <summary>
/// Translates the fixed text written in XAML as each element loads.
/// </summary>
/// <remarks>
/// <para>
/// The views keep their English text exactly as written; when the interface language is not
/// English, every element's literal <c>Text</c>, <c>Content</c>, <c>Header</c>, <c>ToolTip</c> and
/// <c>Title</c> is looked up in the translation table when the element loads. Data-bound values
/// are never touched — those come from view models, which translate their own text — except for
/// the English words of a binding's <c>StringFormat</c>.
/// </para>
/// <para>
/// Keeping the XAML untouched means a view changed upstream merges without conflicts; a new
/// string simply shows in English until it is added to <c>Localization/fr.json</c>, and a test
/// lists any string still missing.
/// </para>
/// </remarks>
internal static class XamlTextTranslator
{
    private static readonly ConditionalWeakTable<DependencyObject, object> Translated = new();
    private static readonly object Marker = new();
    private static bool _registered;

    /// <summary>Installs the translator. Does nothing when the interface is in English.</summary>
    public static void Register()
    {
        if (_registered || UiText.Language == UiLanguage.English)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded),
            handledEventsToo: true);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject element && !Translated.TryGetValue(element, out _))
        {
            Translated.AddOrUpdate(element, Marker);
            Translate(element);
        }
    }

    private static void Translate(DependencyObject element)
    {
        switch (element)
        {
            case TextBlock textBlock:
                if (!TranslateString(textBlock, TextBlock.TextProperty)
                    && !TranslateStringFormat(textBlock, TextBlock.TextProperty))
                {
                    TranslateInlines(textBlock.Inlines);
                }

                break;

            case HeaderedContentControl headered:
                TranslateString(headered, HeaderedContentControl.HeaderProperty);
                TranslateString(headered, ContentControl.ContentProperty);
                break;

            case HeaderedItemsControl headeredItems:
                TranslateString(headeredItems, HeaderedItemsControl.HeaderProperty);
                break;

            case Window window:
                TranslateString(window, Window.TitleProperty);
                break;

            case ContentControl content:
                TranslateString(content, ContentControl.ContentProperty);
                break;
        }

        TranslateString(element, FrameworkElement.ToolTipProperty);
    }

    /// <summary>
    /// Translates a property holding literal text, written on the element or in the template
    /// that created it. Returns true when such text was found.
    /// </summary>
    private static bool TranslateString(DependencyObject element, DependencyProperty property)
    {
        ValueSource source = DependencyPropertyHelper.GetValueSource(element, property);
        if (source.IsExpression
            || source.IsAnimated
            || source.IsCoerced
            || source.BaseValueSource is not (BaseValueSource.Local or BaseValueSource.ParentTemplate))
        {
            return false;
        }

        if (element.GetValue(property) is not string text || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string translated = UiText.T(text);
        if (!string.Equals(translated, text, StringComparison.Ordinal))
        {
            element.SetValue(property, translated);
        }

        return true;
    }

    /// <summary>
    /// Re-creates a simple binding whose <c>StringFormat</c> carries English words, such as
    /// <c>{0} turns</c>, with the translated format.
    /// </summary>
    private static bool TranslateStringFormat(FrameworkElement element, DependencyProperty property)
    {
        if (BindingOperations.GetBinding(element, property) is not Binding binding
            || binding.StringFormat is not { Length: > 0 } format)
        {
            return false;
        }

        string translated = UiText.T(format);
        if (string.Equals(translated, format, StringComparison.Ordinal))
        {
            return true;
        }

        var copy = new Binding
        {
            Path = binding.Path,
            Mode = binding.Mode,
            Converter = binding.Converter,
            ConverterParameter = binding.ConverterParameter,
            ConverterCulture = binding.ConverterCulture,
            TargetNullValue = binding.TargetNullValue,
            FallbackValue = binding.FallbackValue,
            StringFormat = translated,
        };

        if (binding.ElementName is { Length: > 0 } elementName)
        {
            copy.ElementName = elementName;
        }
        else if (binding.RelativeSource is not null)
        {
            copy.RelativeSource = binding.RelativeSource;
        }
        else if (binding.Source is not null)
        {
            copy.Source = binding.Source;
        }

        element.SetBinding(property, copy);
        return true;
    }

    private static void TranslateInlines(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    TranslateString(run, Run.TextProperty);
                    break;
                case Span span:
                    TranslateInlines(span.Inlines);
                    break;
            }
        }
    }
}
