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
