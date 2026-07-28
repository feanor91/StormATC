using System.Windows;
using System.Windows.Controls;
using StormATC.Desktop.ViewModels;

namespace StormATC.Desktop.Views;

/// <summary>
/// The settings page.
/// </summary>
/// <remarks>
/// <para>
/// The two key handlers exist because <see cref="PasswordBox.Password"/> is deliberately not a
/// dependency property: binding it would place the key in the visual tree, where it could be
/// captured by an automation client, a screenshot of the element, or a serialised binding.
/// </para>
/// <para>
/// Instead the value is read on an explicit click, handed straight to the secret store, and the
/// box is cleared. Nothing retains it.
/// </para>
/// </remarks>
public partial class SettingsView : UserControl
{
    /// <summary>Creates the settings page.</summary>
    public SettingsView() => InitializeComponent();

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private async void OnSaveOpenRouterKey(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not SettingsViewModel viewModel)
        {
            return;
        }

        string key = OpenRouterKeyBox.Password;
        OpenRouterKeyBox.Clear();

        await viewModel.SaveOpenRouterKeyAsync(key).ConfigureAwait(true);
    }

    private async void OnSaveOpenAIKey(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not SettingsViewModel viewModel)
        {
            return;
        }

        string key = OpenAIKeyBox.Password;
        OpenAIKeyBox.Clear();
        await viewModel.SaveOpenAIKeyAsync(key).ConfigureAwait(true);
    }

    private async void OnSaveGroqKey(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not SettingsViewModel viewModel)
        {
            return;
        }

        string key = GroqKeyBox.Password;
        GroqKeyBox.Clear();
        await viewModel.SaveGroqKeyAsync(key).ConfigureAwait(true);
    }

    private async void OnSaveDeepSeekKey(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not SettingsViewModel viewModel)
        {
            return;
        }

        string key = DeepSeekKeyBox.Password;
        DeepSeekKeyBox.Clear();
        await viewModel.SaveDeepSeekKeyAsync(key).ConfigureAwait(true);
    }

    private async void OnSaveAnthropicKey(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not SettingsViewModel viewModel)
        {
            return;
        }

        string key = AnthropicKeyBox.Password;
        AnthropicKeyBox.Clear();
        await viewModel.SaveAnthropicKeyAsync(key).ConfigureAwait(true);
    }

    private async void OnSaveDeepgramKey(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not SettingsViewModel viewModel)
        {
            return;
        }

        string key = DeepgramKeyBox.Password;
        DeepgramKeyBox.Clear();

        await viewModel.SaveDeepgramKeyAsync(key).ConfigureAwait(true);
    }
}
