using Avalonia.Input;
using Beutl.Extensions.Voice.ViewModels;
using FluentAvalonia.UI.Controls;

namespace Beutl.Extensions.Voice.Views;

public partial class VoiceVoxInstallDialog : ContentDialog
{
    public VoiceVoxInstallDialog()
    {
        InitializeComponent();
    }

    protected override Type StyleKeyOverride => typeof(ContentDialog);

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
        }

        base.OnKeyUp(e);
    }

    protected override async void OnPrimaryButtonClick(ContentDialogButtonClickEventArgs args)
    {
        base.OnPrimaryButtonClick(args);
        if (DataContext is not VoiceVoxInstallDialogViewModel viewModel) return;
        args.Cancel = true;
        // 同意画面で次へをクリックしたとき
        if (Root.Root.SelectedIndex == 0)
        {
            Root.Root.SelectedIndex = 1;
            PrimaryButtonText = "インストール";
        }
        // ダウンロード画面（未開始）でクリックしたとき
        else if (Root.Root.SelectedIndex == 1)
        {
            if (!viewModel.IsCompleted.Value)
            {
                IsPrimaryButtonEnabled = false;
                await viewModel.Install();
                CloseButtonText = "閉じる";
            }
        }
    }

    protected override void OnCloseButtonClick(ContentDialogButtonClickEventArgs args)
    {
        base.OnCloseButtonClick(args);
        if (DataContext is not VoiceVoxInstallDialogViewModel viewModel) return;
        if (Root.Root.SelectedIndex == 1 && viewModel.IsInstalling.Value)
        {
            viewModel.Cancel();
        }
    }
}