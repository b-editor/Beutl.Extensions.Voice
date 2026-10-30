using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Beutl.Extensions.Voice.Views;

public partial class CoreInstallPromptView : UserControl
{
    public CoreInstallPromptView() => InitializeComponent();

    private void SkipCore(object? sender, RoutedEventArgs e) =>
        TtsLoader.Preferences.Skip(NeverShowAgainCheckBox.IsChecked == true);

    private async void InstallCore(object? sender, RoutedEventArgs e) =>
        await VoiceVoxInstallDialog.ShowFor(this, NeverShowAgainCheckBox.IsChecked == true);
}
