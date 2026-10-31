using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Beutl.Extensions.Voice.Views;

public partial class CoreInstallPromptView : UserControl
{
    public CoreInstallPromptView() => InitializeComponent();

    private void FindLocalEngines(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<TtsTabView>() is { } tab)
        {
            if (NeverShowAgainCheckBox.IsChecked == true) TtsLoader.Preferences.Skip(true);
            tab.ShowSettings();
        }
    }

    private void SkipCore(object? sender, RoutedEventArgs e) =>
        TtsLoader.Preferences.Skip(NeverShowAgainCheckBox.IsChecked == true);

    private async void InstallCore(object? sender, RoutedEventArgs e) =>
        await VoiceVoxInstallDialog.ShowFor(this, NeverShowAgainCheckBox.IsChecked == true);
}
