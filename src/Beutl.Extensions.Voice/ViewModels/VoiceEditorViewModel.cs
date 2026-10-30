using System.Reactive.Disposables;
using System.Text.Json;
using Avalonia.Threading;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

namespace Beutl.Extensions.Voice.ViewModels;

// Shared editing behavior for new speech and an existing TtsController.
public class VoiceEditorViewModel : IDisposable
{
    protected readonly CompositeDisposable _subscriptions = new();
    protected readonly TaskCompletionSource _initTcs = new();
    protected bool _disposed;
    private bool _updating;
    private VoiceSettings? _unavailableSettings;

    public VoiceEditorViewModel()
    {
        Text.Subscribe(_ => { if (!_updating) InvalidateQuery(); }).AddTo(_subscriptions);
        SelectedVoice.Subscribe(voice =>
        {
            if (_updating) return;
            _unavailableSettings = null;
            UpdateSilently(() =>
            {
                if (voice?.Styles.Contains(SelectedStyle.Value!) != true)
                    SelectedStyle.Value = voice?.Styles.FirstOrDefault();
            });
            InvalidateQuery();
            Edited?.Invoke();
            ConfirmEdit();
        }).AddTo(_subscriptions);
        SelectedStyle.Subscribe(_ =>
        {
            if (_updating) return;
            InvalidateQuery();
            Edited?.Invoke();
            ConfirmEdit();
        }).AddTo(_subscriptions);
        foreach (var property in new[] { SpeedScale, PitchScale, IntonationScale, VolumeScale, PrePhonemeLength, PostPhonemeLength })
            property.Subscribe(_ => { if (!_updating) Edited?.Invoke(); }).AddTo(_subscriptions);
        AudioQuery.Subscribe(query => HasAudioQuery.Value = query != null).AddTo(_subscriptions);
        TtsLoader.VoiceVoxLoader.Subscribe(async loader =>
        {
            if (loader == null) return;
            await loader.InitializationTcs.Task;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed && ReferenceEquals(loader, TtsLoader.VoiceVoxLoader.Value)) OnLoaded();
            });
        }).AddTo(_subscriptions);
    }

    public ReactivePropertySlim<string> Text { get; } = new("");
    public ReactivePropertySlim<VoiceMetadata[]> Voice { get; } = new([]);
    public ReactivePropertySlim<VoiceMetadata?> SelectedVoice { get; } = new();
    public ReactivePropertySlim<VoiceStyle?> SelectedStyle { get; } = new();
    public ReactivePropertySlim<bool> IsGenerating { get; } = new();
    public ReactivePropertySlim<bool> IsEnabled { get; } = new();
    public ReactivePropertySlim<bool> IsVoiceVoxInstalled { get; } = new(true);
    public ReactivePropertySlim<AudioQueryModel?> AudioQuery { get; } = new();
    public ReactivePropertySlim<bool> HasAudioQuery { get; } = new();
    public ReactivePropertySlim<double> SpeedScale { get; } = new(1);
    public ReactivePropertySlim<double> PitchScale { get; } = new();
    public ReactivePropertySlim<double> IntonationScale { get; } = new(1);
    public ReactivePropertySlim<double> VolumeScale { get; } = new(1);
    public ReactivePropertySlim<double> PrePhonemeLength { get; } = new(0.1);
    public ReactivePropertySlim<double> PostPhonemeLength { get; } = new(0.1);
    public ReactivePropertySlim<string> Message { get; } = new("");
    protected event Action? Edited;
    protected event Action? EditCompleted;

    public void ConfirmEdit() => EditCompleted?.Invoke();

    public void OnLoaded()
    {
        try
        {
            var loader = TtsLoader.VoiceVoxLoader.Value;
            IsVoiceVoxInstalled.Value = loader?.IsInstalled == true;
            IsEnabled.Value = loader?.IsLoaded == true;
            if (loader?.IsLoaded != true) return;
            var previous = CaptureStoredSettings();
            var query = AudioQuery.Value;
            UpdateSilently(() =>
            {
                Voice.Value = loader.VoiceSets.SelectMany(x => x.Metadata).GroupBy(x => x.SpeakerUuid)
                    .Select(group => new VoiceMetadata
                    {
                        Name = group.First().Name, Version = group.First().Version, SpeakerUuid = group.Key,
                        Styles = group.SelectMany(x => x.Styles).ToArray()
                    }).ToArray();
                if (previous != null) ApplySettings(previous, report: false);
                // Replacing the choices temporarily clears ComboBox selections, not the analyzed voice.
                AudioQuery.Value = query;
            });
        }
        catch (Exception ex) { Message.Value = $"VOICEVOXの初期化に失敗しました: {ex.Message}"; }
        finally { _initTcs.TrySetResult(); }
    }

    protected VoiceSettings? CaptureSettings()
    {
        var voice = SelectedVoice.Value;
        var style = SelectedStyle.Value;
        if (voice == null || style == null || !voice.Styles.Contains(style)) return null;
        return WithTuning(new VoiceSettings
        {
            SpeakerUuid = voice.SpeakerUuid, SpeakerName = voice.Name, StyleId = style.Id,
            StyleName = style.Name, ModelVersion = voice.Version
        });
    }

    protected VoiceSettings? CaptureStoredSettings() => CaptureSettings()
        ?? (_unavailableSettings == null ? null : WithTuning(_unavailableSettings));

    // Tuning can be edited and saved before a voice has been selected.
    protected VoiceSettings CaptureDraftSettings() => CaptureStoredSettings() ?? WithTuning(new VoiceSettings());

    private VoiceSettings WithTuning(VoiceSettings settings) => settings with
    {
        SpeedScale = SpeedScale.Value, PitchScale = PitchScale.Value, IntonationScale = IntonationScale.Value,
        VolumeScale = VolumeScale.Value, PrePhonemeLength = PrePhonemeLength.Value, PostPhonemeLength = PostPhonemeLength.Value
    };

    protected bool ApplySettings(VoiceSettings settings, bool report = true)
    {
        bool sameVoice = CaptureStoredSettings()?.UsesSameVoice(settings) == true;
        var voice = settings.EngineId == VoiceSettings.CoreEngineId
            ? Voice.Value.FirstOrDefault(x => x.SpeakerUuid == settings.SpeakerUuid) : null;
        var style = voice?.Styles.FirstOrDefault(x => x.Id == settings.StyleId);
        UpdateSilently(() =>
        {
            SelectedVoice.Value = voice;
            SelectedStyle.Value = style;
            SpeedScale.Value = settings.SpeedScale;
            PitchScale.Value = settings.PitchScale;
            IntonationScale.Value = settings.IntonationScale;
            VolumeScale.Value = settings.VolumeScale;
            PrePhonemeLength.Value = settings.PrePhonemeLength;
            PostPhonemeLength.Value = settings.PostPhonemeLength;
            _unavailableSettings = style == null && !string.IsNullOrEmpty(settings.SpeakerUuid) ? settings : null;
        });
        if (!sameVoice) InvalidateQuery();
        if (report)
            Message.Value = style == null
                ? $"保存された声が見つかりません: {settings.EngineId} / {settings.SpeakerName} / {settings.StyleName}。話者を選び直してください。"
                : voice!.Version != settings.ModelVersion
                    ? $"モデルのバージョンが異なります（保存時 {settings.ModelVersion} → 現在 {voice.Version}）。"
                    : "声の設定を適用しました。";
        return style != null;
    }

    protected void RestoreState(string text, VoiceSettings? settings, string? queryJson)
    {
        UpdateSilently(() =>
        {
            Text.Value = text;
            ApplySettings(settings ?? new VoiceSettings(), report: false);
            if (settings == null) _unavailableSettings = null;
            AudioQuery.Value = null;
            if (!string.IsNullOrEmpty(queryJson))
            {
                try { AudioQuery.Value = JsonSerializer.Deserialize<AudioQueryModel>(queryJson); }
                catch (JsonException) { /* Older snapshots can be analyzed again. */ }
            }
        });
    }

    private void UpdateSilently(Action update)
    {
        bool previous = _updating;
        _updating = true;
        try { update(); }
        finally { _updating = previous; }
    }

    private void InvalidateQuery() => AudioQuery.Value = null;

    protected (string Text, VoiceSettings Settings)? CaptureRequest()
    {
        if (string.IsNullOrWhiteSpace(Text.Value)) { Message.Value = "テキストを入力してください。"; return null; }
        if (CaptureSettings() is not { } settings) { Message.Value = "話者とスタイルを選択してください。"; return null; }
        return (Text.Value, settings);
    }

    public virtual async Task CreateQuery()
    {
        if (_disposed || IsGenerating.Value || CaptureRequest() is not { } request) return;
        IsGenerating.Value = true;
        Message.Value = "";
        try
        {
            var query = await Task.Run(() => VoiceSynthesisService.CreateQuery(request.Text, request.Settings));
            PublishQuery(request.Text, request.Settings, query);
        }
        catch (Exception ex) { Message.Value = $"テキストを解析できませんでした: {ex.Message}"; }
        finally { IsGenerating.Value = false; }
    }

    protected void PublishQuery(string text, VoiceSettings settings, AudioQueryModel query)
    {
        if (_disposed || Text.Value != text || CaptureSettings()?.UsesSameVoice(settings) != true) return;
        AudioQuery.Value = query;
        Edited?.Invoke();
        ConfirmEdit();
    }

    protected string SerializeQuery() => AudioQuery.Value is { } query
        ? JsonSerializer.Serialize(query, VoiceSynthesisService.JsonOptions) : "";

    protected AudioQueryModel? CopyCurrentQuery() => AudioQuery.Value == null ? null
        : JsonSerializer.Deserialize<AudioQueryModel>(SerializeQuery());

    public void OnAccentPhrasesUpdated()
    {
        AudioQuery.ForceNotify();
        Edited?.Invoke();
        ConfirmEdit();
    }

    public virtual void Dispose()
    {
        _disposed = true;
        _subscriptions.Dispose();
        _initTcs.TrySetResult();
        Edited = null;
        EditCompleted = null;
    }
}
