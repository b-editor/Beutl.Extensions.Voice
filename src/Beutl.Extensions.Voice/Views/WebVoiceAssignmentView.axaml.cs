using Avalonia.Controls;
using Avalonia.Threading;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.ViewModels;
using FluentAvalonia.UI.Controls;

namespace Beutl.Extensions.Voice.Views;

public partial class WebVoiceAssignmentView : UserControl
{
    public WebVoiceAssignmentView() => InitializeComponent();

    private void RefreshTargets(object? sender, EventArgs e)
    {
        if (DataContext is WebVoiceAssignmentViewModel model) model.LipSync.Refresh();
    }

    private void RefreshTemplates(object? sender, EventArgs e)
    {
        if (DataContext is WebVoiceAssignmentViewModel model) model.Subtitles.Refresh();
    }
}

internal static class WebVoiceAssignmentDialog
{
    public static Task<WebVoiceAssignment?> ShowAsync(WebVoiceAssignmentViewModel model, CancellationToken cancellationToken) =>
        ShowAsync(model, "追加", null, cancellationToken);

    public static Task<WebVoiceAssignment?> ShowAsync(WebVoiceAssignmentViewModel model, string primaryButtonText,
        CancellationToken cancellationToken) => ShowAsync(model, primaryButtonText, null, cancellationToken);

    // Returns null when the user cancels. An invalid choice keeps the dialog open with the reason.
    // Without an owner the dialog opens over the active window.
    public static async Task<WebVoiceAssignment?> ShowAsync(WebVoiceAssignmentViewModel model, string primaryButtonText,
        TopLevel? owner, CancellationToken cancellationToken)
    {
        WebVoiceAssignment? result = null;
        var dialog = new FAContentDialog
        {
            Title = "立ち絵と字幕の割り当て",
            Content = new WebVoiceAssignmentView { DataContext = model },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "キャンセル",
            DefaultButton = FAContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            result = model.TryCapture();
            if (result == null) args.Cancel = true;
        };
        await using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.Hide));
        var answer = owner != null ? await dialog.ShowAsync(owner) : await dialog.ShowAsync();
        return answer == FAContentDialogResult.Primary && !cancellationToken.IsCancellationRequested ? result : null;
    }
}
