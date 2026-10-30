using Avalonia.Controls;
using Beutl.Extensions.Voice.ViewModels;
using FluentAvalonia.UI.Controls;

namespace Beutl.Extensions.Voice.Views;

public partial class VoiceVoxInstallDialog : FAContentDialog
{
    public VoiceVoxInstallDialog()
    {
        InitializeComponent();
    }

    protected override Type StyleKeyOverride => typeof(FAContentDialog);

    internal static Task<FAContentDialogResult> ShowFor(Control? owner = null, bool? doNotShowAgain = null)
    {
        var model = new VoiceVoxInstallDialogViewModel();
        if (doNotShowAgain.HasValue) model.DoNotShowAgain.Value = doNotShowAgain.Value;
        var dialog = new VoiceVoxInstallDialog { DataContext = model };
        return owner != null && TopLevel.GetTopLevel(owner) is { } topLevel ? dialog.ShowAsync(topLevel) : dialog.ShowAsync();
    }

    protected override async void OnPrimaryButtonClick(FAContentDialogButtonClickEventArgs args)
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
                CloseButtonText = "キャンセル";
                await viewModel.Install();
                CloseButtonText = "閉じる";
            }
        }
    }

    protected override void OnClosing(FAContentDialogClosingEventArgs args)
    {
        base.OnClosing(args);
        if (args.Cancel || DataContext is not VoiceVoxInstallDialogViewModel viewModel) return;
        TtsLoader.Preferences.HideCoreInstallPrompt.Value = viewModel.DoNotShowAgain.Value;
        // A failed attempt must allow retry, including after the startup notification was dismissed.
        if (!string.IsNullOrEmpty(viewModel.Error.Value)) TtsLoader.Preferences.BeginSession();
        else TtsLoader.Preferences.Skip(false);
        viewModel.Cancel();
    }
}
