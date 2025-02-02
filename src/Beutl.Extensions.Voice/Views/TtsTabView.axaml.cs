using Avalonia.Controls;
using Avalonia.Interactivity;
using Beutl.Extensions.Voice.ViewModels;

namespace Beutl.Extensions.Voice.Views;

public partial class TtsTabView : UserControl
{
    public TtsTabView()
    {
        InitializeComponent();
    }

    private async void DownloadVoiceVox(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TtsTabViewModel viewModel) return;
        var dialogViewModel = new VoiceVoxInstallDialogViewModel();
        var dialog = new VoiceVoxInstallDialog
        {
            DataContext = dialogViewModel
        };
        await dialog.ShowAsync();
        viewModel.OnLoaded();
    }
}