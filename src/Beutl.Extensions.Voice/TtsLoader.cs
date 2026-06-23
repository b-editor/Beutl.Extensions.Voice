using Beutl.Extensibility;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.Logging;
using Beutl.Services;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice;

[Export]
public class TtsLoader : Extension
{
    private readonly ILogger _logger = Log.CreateLogger<TtsLoader>();
    internal static readonly ReactiveProperty<VoiceVoxLoader?> VoiceVoxLoader = new();

    public override string Name => "TTS Loader";

    public override string DisplayName => Name;

    public override void Load()
    {
        base.Load();
        _ = StaticLoad().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception, "Failed to load TTS");
            }

            if (!t.IsFaulted && VoiceVoxLoader.Value?.IsInstalled != true)
            {
                Dispatcher.UIThread.Post(() =>
                    NotificationService.ShowWarning(
                        title: "警告",
                        message: "VOICEVOXがインストールされていません。",
                        actionButtonText: "インストール",
                        onActionButtonClick: ShowInstallDialog));
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private async void ShowInstallDialog()
    {
        var dialogViewModel = new VoiceVoxInstallDialogViewModel();
        var dialog = new VoiceVoxInstallDialog { DataContext = dialogViewModel };
        await dialog.ShowAsync();
    }

    public static Task StaticLoad()
    {
        var home = BeutlEnvironment.GetHomeDirectoryPath();
        var voicevoxHomePath = Path.Combine(home, "voicevox");
        VoiceVoxLoader.Value = new VoiceVoxLoader(voicevoxHomePath);
        return Task.Run(() => VoiceVoxLoader.Value.Load());
    }
}
