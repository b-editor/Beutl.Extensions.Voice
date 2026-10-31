using System.Text.Json.Nodes;
using Beutl.Audio;
using Beutl.Editor;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.Logging;
using Beutl.Media.Source;
using Beutl.ProjectSystem;
using Microsoft.Extensions.Logging;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

namespace Beutl.Extensions.Voice.ViewModels;

public partial class TtsControllerViewModel : VoiceEditorViewModel, IPropertyEditorContext
{
    private readonly TtsController _controller;
    private readonly ILogger _logger = Log.CreateLogger<TtsControllerViewModel>();
    private HistoryManager? _history;
    private bool _writingDraft;

    public TtsControllerViewModel(IPropertyEditorContext inner, PropertyEditorExtension extension, TtsController controller)
    {
        Inner = inner;
        Extension = extension;
        _controller = controller;
        _lipSyncHistory.AddTo(_subscriptions);
        SelectedPreset.Subscribe(value => HasSelectedPreset.Value = value != null).AddTo(_subscriptions);
        controller.AppliedPreset.GetObservable().Subscribe(_ => RefreshDetails()).AddTo(_subscriptions);
        controller.Draft.GetObservable().Subscribe(_ => RefreshDetails()).AddTo(_subscriptions);
        controller.Generation.GetObservable().Subscribe(_ => RefreshDetails()).AddTo(_subscriptions);
        controller.Text.GetObservable().Subscribe(_ => RefreshDetails()).AddTo(_subscriptions);
        controller.LipSyncGroupId.GetObservable().Subscribe(_ => RefreshLipSyncTargets()).AddTo(_subscriptions);
        Edited += SaveDraft;
        EditCompleted += () => _history?.Commit();
        RefreshPresets();
    }

    public IPropertyEditorContext Inner { get; }
    public PropertyEditorExtension Extension { get; }

    public ReactivePropertySlim<VoicePreset[]> Presets { get; } = new([]);
    public ReactivePropertySlim<VoicePreset?> SelectedPreset { get; } = new();
    public ReactivePropertySlim<bool> HasSelectedPreset { get; } = new();
    public ReactivePropertySlim<bool> HasSettings { get; } = new();
    public ReactivePropertySlim<bool> HasEditor { get; } = new();
    public ReactivePropertySlim<string> GenerationSettings { get; } = new("");

    public void RefreshPresets()
    {
        var id = SelectedPreset.Value?.Id ?? _controller.AppliedPreset.Value?.Id ?? _controller.Generation.Value?.PresetId;
        try
        {
            Presets.Value = VoicePresetStore.Load(VoicePresetStore.GlobalPath);
            SelectedPreset.Value = Presets.Value.FirstOrDefault(x => x.Id == id);
            Message.Value = "";
        }
        catch (Exception ex)
        {
            Message.Value = $"プリセットを読み込めませんでした: {ex.Message}";
        }
    }

    public void ApplyPreset()
    {
        if (IsGenerating.Value || _disposed || SelectedPreset.Value is not { } preset) return;
        try
        {
            GetEditableTarget();
            var draft = new VoiceGeneration
            {
                Text = _controller.Text.Value, Settings = preset.Settings, PresetId = preset.Id, PresetName = preset.Name,
                AudioQueryJson = CaptureStoredSettings()?.UsesSameVoice(preset.Settings) == true ? SerializeQuery() : ""
            };
            _history!.ExecuteInTransaction(() =>
            {
                _controller.AppliedPreset.Value = preset;
                _controller.Draft.Value = draft;
            });
            Message.Value = "プリセットを適用しました。「再生成」で音声に反映します。";
        }
        catch (Exception ex)
        {
            Message.Value = ex.Message;
        }
    }

    private void RefreshDetails()
    {
        var generation = _controller.Generation.Value;
        var draft = _controller.Draft.Value;
        var settings = draft?.Settings ?? _controller.AppliedPreset.Value?.Settings ?? generation?.Settings;
        HasSettings.Value = !string.IsNullOrEmpty(settings?.SpeakerUuid);
        GenerationSettings.Value = generation == null ? "生成時の設定が保存されていません。"
            : $"プリセット: {generation.PresetName ?? "なし"}\n{generation.Settings.Describe()}\n本文: {generation.Text}";
        if (_writingDraft) return;
        // Editing text invalidates the visible query, but Undo can restore its saved accents.
        var query = new[] { draft, generation }.FirstOrDefault(x => x != null && settings != null
            && x.Text == _controller.Text.Value && x.Settings.UsesSameVoice(settings));
        RestoreState(_controller.Text.Value, settings, query?.AudioQueryJson);
    }

    private void SaveDraft()
    {
        if (_disposed) return;
        try
        {
            GetEditableTarget();
            var settings = CaptureDraftSettings();
            _writingDraft = true;
            _controller.Draft.Value = new VoiceGeneration
            {
                Text = _controller.Text.Value, Settings = settings, AudioQueryJson = SerializeQuery(),
                PresetId = _controller.AppliedPreset.Value?.Id ?? _controller.Generation.Value?.PresetId,
                PresetName = _controller.AppliedPreset.Value?.Name ?? _controller.Generation.Value?.PresetName
            };
        }
        catch (Exception ex)
        {
            _writingDraft = false;
            RefreshDetails();
            Message.Value = ex.Message;
        }
        finally { _writingDraft = false; }
    }

    public override Task CreateQuery()
    {
        try
        {
            GetEditableTarget();
            _history!.Commit();
            return base.CreateQuery();
        }
        catch (Exception ex)
        {
            Message.Value = ex.Message;
            return Task.CompletedTask;
        }
    }

    private (Element Element, Scene Scene) GetEditableTarget()
    {
        var element = _controller.FindHierarchicalParent<Element>();
        var scene = element?.FindHierarchicalParent<Scene>();
        if (_history == null || element == null || scene == null || !element.Objects.Contains(_controller))
            throw new InvalidOperationException("編集する読み上げ要素が見つかりません。");
        if (scene.IsElementLocked(element))
            throw new InvalidOperationException("読み上げ要素またはレイヤーがロックされています。");
        return (element, scene);
    }

    public async Task Regenerate()
    {
        if (IsGenerating.Value || _disposed) return;
        IsGenerating.Value = true;
        Message.Value = "";
        string? path = null;
        bool committed = false;
        try
        {
            var (element, scene) = GetEditableTarget();
            var sound = element.Objects.OfType<SourceSound>().FirstOrDefault()
                ?? throw new InvalidOperationException("読み上げ要素の音声が見つかりません。");
            var source = sound.Source.Value;
            var preset = _controller.AppliedPreset.Value;
            var generation = _controller.Generation.Value;
            var draft = _controller.Draft.Value;
            var settings = CaptureStoredSettings()
                ?? throw new InvalidOperationException("話者を選ぶか、声のプリセットを適用してください。");
            string text = _controller.Text.Value;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("テキストを入力してください。");

            var query = CopyCurrentQuery();
            string directory = VoiceSynthesisService.GetOutputDirectory(scene);
            var output = await VoiceSynthesisService.SynthesizeAsync(text, settings, query);
            if (_disposed) return;
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"{Guid.NewGuid()}.wav");
            await File.WriteAllBytesAsync(path, output.Wave);
            if (_disposed) return;
            var current = GetEditableTarget();
            if (current.Element != element || current.Scene != scene || !element.Objects.Contains(sound)
                || sound.Source.Value != source || _controller.Text.Value != text
                || _controller.AppliedPreset.Value != preset || _controller.Generation.Value != generation
                || _controller.Draft.Value != draft)
                throw new InvalidOperationException("生成中に対象の設定が変わりました。もう一度再生成してください。");

            var nextSource = SoundSource.Open(path);
            _history!.ExecuteInTransaction(() =>
            {
                sound.Source.Value = nextSource;
                _controller.Generation.Value = new VoiceGeneration
                {
                    Text = text, Settings = output.Settings, AudioQueryJson = output.Json,
                    PresetId = preset?.Id ?? generation?.PresetId,
                    PresetName = preset?.Name ?? generation?.PresetName
                };
                _controller.Draft.Value = _controller.Generation.Value;
            });
            committed = true;
            Message.Value = "音声を再生成しました。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to regenerate TTS");
            Message.Value = $"再生成できませんでした: {ex.Message}";
        }
        finally
        {
            if (!committed && path != null)
                try { File.Delete(path); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete generated speech file {Path}", path); }
            IsGenerating.Value = false;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        Inner.Dispose();
    }

    public void WriteToJson(JsonObject json)
    {
        Inner.WriteToJson(json);
    }

    public void ReadFromJson(JsonObject json)
    {
        Inner.ReadFromJson(json);
    }

    public void Accept(IPropertyEditorContextVisitor visitor)
    {
        visitor.Visit(this);
        Inner.Accept(visitor);
        if (visitor is IServiceProvider services)
        {
            _history = services.GetService(typeof(HistoryManager)) as HistoryManager;
            // The target expressions and the group can replay in either order during Undo/Redo.
            _lipSyncHistory.Disposable = _history?.StateChanged.Subscribe(_ => RefreshLipSyncTargets());
            HasEditor.Value = _history != null;
            RefreshLipSyncTargets();
        }
    }
}
