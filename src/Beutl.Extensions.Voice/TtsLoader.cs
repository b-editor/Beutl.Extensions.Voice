using Beutl.Extensibility;
using Beutl.Extensions.Voice.Services;
using Beutl.Extensions.Voice.ViewModels;
using Beutl.Extensions.Voice.Views;
using Beutl.Logging;
using Beutl.Services;
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
        StaticLoad().ContinueWith(t =>
        {
            if (VoiceVoxLoader.Value?.IsInstalled != true)
            {
                NotificationService.ShowWarning(
                    title: "警告",
                    message:"VOICEVOXがインストールされていません。",
                    actionButtonText:   "インストール",
                    onActionButtonClick: ShowInstallDialog);
            }
        });
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
        var voicevoxCorePath = Path.Combine(home, "voicevox_core");
        VoiceVoxLoader.Value = new VoiceVoxLoader(voicevoxCorePath);
        return Task.Run(() => VoiceVoxLoader.Value.Load());
    }
}