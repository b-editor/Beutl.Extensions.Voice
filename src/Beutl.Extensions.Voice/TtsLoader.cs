using Beutl.Extensibility;
using Beutl.Extensions.Voice.Services;
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
    private CancellationTokenSource? _installNotification;
    internal static readonly ReactiveProperty<VoiceVoxLoader?> VoiceVoxLoader = new();
    internal static VoiceExtensionSettings Preferences { get; } = new();

    public override VoiceExtensionSettings Settings => Preferences;

    public override string Name => "TTS Loader";

    public override string DisplayName => Name;

    public override void Load()
    {
        base.Load();
        Preferences.BeginSession();
        _installNotification = new();
        Preferences.PromptChanged += OnPromptChanged;
        _ = StaticLoad().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception, "Failed to load TTS");
            }

            if (!t.IsFaulted)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (_installNotification is { IsCancellationRequested: false } notification
                            && CreateInstallNotification(notification.Token) is { } prompt)
                            NotificationService.Show(prompt);
                    }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to show Core setup prompt"); }
                });
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    internal static Notification? CreateInstallNotification(CancellationToken cancellationToken = default)
    {
        if (!Preferences.ShouldPrompt || VoiceVoxLoader.Value?.IsInstalled == true)
            return null;
        string message = "外部エンジンだけを使う場合は不要です。";
        try
        {
            if (VoiceEngineStore.Load().Length > 0) return null;
        }
        catch (Exception ex)
        {
            Log.CreateLogger<TtsLoader>().LogError(ex, "Failed to load voice engine connections");
            message = "外部エンジンの接続設定を読み込めませんでした。VOICEVOX Coreは必要な場合だけインストールしてください。";
        }
        return new Notification("VOICEVOX Coreのセットアップ", message,
            OnClose: () => Preferences.Skip(false),
            Actions:
            [
                new("インストール", async () => await VoiceVoxInstallDialog.ShowFor()),
                new("今はスキップ", () => Preferences.Skip(false)),
                new("今後表示しない", () => Preferences.Skip(true))
            ]) { CancellationToken = cancellationToken };
    }

    private void OnPromptChanged()
    {
        if (!Preferences.ShouldPrompt) _installNotification?.Cancel();
    }

    public override void Unload()
    {
        Preferences.PromptChanged -= OnPromptChanged;
        _installNotification?.Cancel();
        _installNotification?.Dispose();
        _installNotification = null;
        base.Unload();
    }

    public static Task StaticLoad()
    {
        var home = BeutlEnvironment.GetHomeDirectoryPath();
        var voicevoxHomePath = Path.Combine(home, "voicevox");
        VoiceVoxLoader.Value = new VoiceVoxLoader(voicevoxHomePath);
        return Task.Run(() => VoiceVoxLoader.Value.Load());
    }
}
