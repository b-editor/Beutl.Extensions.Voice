using System.Reactive.Disposables;
using System.Text.Json;
using Beutl.Editor.Services;
using Beutl.Extensibility;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Operators;
using Beutl.Extensions.Voice.Services;
using Beutl.ProjectSystem;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

namespace Beutl.Extensions.Voice.ViewModels;

public partial class TtsTabViewModel
{
    private Element? _editingElement;
    private VoiceSettings? _unavailableSettings;
    private readonly SerialDisposable _editingSubscription = new();

    public string[] PresetScopes { get; } = ["全体で共有", "このプロジェクト／シーン"];
    public ReactiveProperty<int> PresetScope { get; } = new();
    public ReactiveProperty<VoicePreset[]> Presets { get; } = new([]);
    public ReactiveProperty<VoicePreset?> SelectedPreset { get; } = new();
    public ReactiveProperty<string> PresetName { get; } = new("");
    public ReactiveProperty<string> PresetDetails { get; } = new("");
    public ReactiveProperty<string> PresetMessage { get; } = new("");
    public ReactiveProperty<bool> HasSelectedPreset { get; } = new();
    public ReactiveProperty<bool> HasEditingElement { get; } = new();
    public ReactiveProperty<string> EditingElementName { get; } = new("");
    public ReactiveProperty<string> GenerationDetails { get; } = new("");

    private void InitializePresets()
    {
        _subscriptions.Add(_editingSubscription);
        PresetScope.Subscribe(_ => RefreshPresets()).AddTo(_subscriptions);
        SelectedPreset.Subscribe(preset =>
        {
            PresetName.Value = preset?.Name ?? "";
            HasSelectedPreset.Value = preset != null;
            RefreshPresetDetails();
        }).AddTo(_subscriptions);
        Text.Subscribe(_ => InvalidateQuery()).AddTo(_subscriptions);
        SelectedVoice.Subscribe(voice =>
        {
            _unavailableSettings = null;
            if (voice?.Styles.Contains(SelectedStyle.Value!) != true)
                SelectedStyle.Value = voice?.Styles.FirstOrDefault();
            InvalidateQuery();
            RefreshPresetDetails();
        }).AddTo(_subscriptions);
        SelectedStyle.Subscribe(_ =>
        {
            InvalidateQuery();
            RefreshPresetDetails();
        }).AddTo(_subscriptions);
        foreach (var property in new[] { SpeedScale, PitchScale, IntonationScale, VolumeScale, PrePhonemeLength, PostPhonemeLength })
            property.Subscribe(_ => RefreshPresetDetails()).AddTo(_subscriptions);
    }

    private void InvalidateQuery()
    {
        AudioQuery.Value = null;
        HasAudioQuery.Value = false;
    }

    private string GetPresetPath()
    {
        if (PresetScope.Value == 0)
            return Path.Combine(BeutlEnvironment.GetHomeDirectoryPath(), "voice", "presets.json");
        var uri = _scene.FindHierarchicalParent<Project>()?.Uri ?? _scene.Uri;
        if (uri is not { IsFile: true }) throw new InvalidOperationException("先にプロジェクトまたはシーンを保存してください。");
        return Path.ChangeExtension(uri.LocalPath, ".voice-presets.json");
    }

    public void RefreshPresets()
    {
        var id = SelectedPreset.Value?.Id;
        Presets.Value = [];
        SelectedPreset.Value = null;
        try
        {
            Presets.Value = VoicePresetStore.Load(GetPresetPath());
            SelectedPreset.Value = Presets.Value.FirstOrDefault(x => x.Id == id);
            PresetMessage.Value = "";
        }
        catch (Exception ex)
        {
            PresetMessage.Value = $"プリセットを読み込めませんでした: {ex.Message}";
        }
    }

    public void AddPreset() => EditPreset(() =>
    {
        var settings = CaptureSettings() ?? throw new InvalidOperationException("話者とスタイルを選択してください。");
        return new VoicePreset { Name = RequirePresetName(), Settings = settings };
    });

    public void DuplicatePreset() => EditPreset(() =>
    {
        var preset = RequirePreset();
        return preset with { Id = Guid.NewGuid(), Name = preset.Name + " のコピー" };
    });

    public void RenamePreset() => EditPreset(() => RequirePreset() with { Name = RequirePresetName() });

    public void UpdatePreset() => EditPreset(() => RequirePreset() with
    {
        Settings = CaptureSettings() ?? throw new InvalidOperationException("話者とスタイルを選択してください。")
    });

    private VoicePreset RequirePreset() => SelectedPreset.Value
        ?? throw new InvalidOperationException("プリセットを選択してください。");

    private string RequirePresetName() => string.IsNullOrWhiteSpace(PresetName.Value)
        ? throw new InvalidOperationException("プリセット名を入力してください。") : PresetName.Value.Trim();

    private void EditPreset(Func<VoicePreset> create)
    {
        try
        {
            var preset = create();
            VoicePresetStore.Save(GetPresetPath(), preset);
            RefreshPresets();
            SelectedPreset.Value = Presets.Value.First(x => x.Id == preset.Id);
            PresetMessage.Value = "保存しました。既存の音声は変更されません。";
        }
        catch (Exception ex)
        {
            PresetMessage.Value = $"プリセットを保存できませんでした: {ex.Message}";
        }
    }

    public void DeletePreset()
    {
        try
        {
            VoicePresetStore.Delete(GetPresetPath(), RequirePreset().Id);
            RefreshPresets();
            PresetMessage.Value = "削除しました。既存の音声と生成時設定は保持されています。";
        }
        catch (Exception ex)
        {
            PresetMessage.Value = $"プリセットを削除できませんでした: {ex.Message}";
        }
    }

    public void ApplyPreset()
    {
        if (SelectedPreset.Value is { } preset) ApplySettings(preset.Settings);
    }

    private VoiceSettings? CaptureSettings()
    {
        var voice = SelectedVoice.Value;
        var style = SelectedStyle.Value;
        if (voice == null || style == null || !voice.Styles.Contains(style)) return null;
        return new VoiceSettings
        {
            SpeakerUuid = voice.SpeakerUuid, SpeakerName = voice.Name,
            StyleId = style.Id, StyleName = style.Name, ModelVersion = voice.Version,
            SpeedScale = SpeedScale.Value, PitchScale = PitchScale.Value,
            IntonationScale = IntonationScale.Value, VolumeScale = VolumeScale.Value,
            PrePhonemeLength = PrePhonemeLength.Value, PostPhonemeLength = PostPhonemeLength.Value
        };
    }

    private bool ApplySettings(VoiceSettings settings)
    {
        var voice = settings.EngineId == VoiceSettings.CoreEngineId
            ? Voice.Value.FirstOrDefault(x => x.SpeakerUuid == settings.SpeakerUuid) : null;
        var style = voice?.Styles.FirstOrDefault(x => x.Id == settings.StyleId);
        bool sameVoice = CaptureSettings()?.UsesSameVoice(settings) == true;
        SelectedVoice.Value = voice;
        SelectedStyle.Value = style;
        SpeedScale.Value = settings.SpeedScale;
        PitchScale.Value = settings.PitchScale;
        IntonationScale.Value = settings.IntonationScale;
        VolumeScale.Value = settings.VolumeScale;
        PrePhonemeLength.Value = settings.PrePhonemeLength;
        PostPhonemeLength.Value = settings.PostPhonemeLength;
        if (!sameVoice) InvalidateQuery();
        _unavailableSettings = style == null ? settings : null;
        PresetMessage.Value = style == null
            ? $"保存された声が見つかりません: {settings.EngineId} / {settings.SpeakerName} / {settings.StyleName}。話者を選び直してください。"
            : voice!.Version != settings.ModelVersion
                ? $"モデルのバージョンが異なります（保存時 {settings.ModelVersion} → 現在 {voice.Version}）。"
                : "設定を適用しました。既存の音声への反映は「再生成」で行います。";
        RefreshPresetDetails();
        return style != null;
    }

    private void RefreshPresetDetails()
    {
        var preset = SelectedPreset.Value;
        PresetDetails.Value = preset == null ? "" : preset.Settings.Describe() + "\n\n" +
            (CaptureSettings() is { } current ? preset.Settings.DescribeDifferences(current) : "現在の話者が未選択です。");
    }

    public void LoadSelectedSpeech()
    {
        var selection = _editorContext.GetService(typeof(IEditorSelection)) as IEditorSelection;
        var element = selection?.SelectedObject.Value as Element
            ?? (selection?.SelectedObject.Value as Hierarchical)?.FindHierarchicalParent<Element>();
        var controller = element?.Objects.OfType<TtsController>().FirstOrDefault();
        if (element == null || controller == null)
        {
            ShowWarning("タイムラインで読み上げ要素を1つ選択してください。");
            return;
        }

        _editingElement = element;
        HasEditingElement.Value = true;
        EditingElementName.Value = element.Name.Value;
        Text.Value = controller.Text.Value;
        InvalidateQuery();
        var generation = controller.Generation.Value;
        SelectedPreset.Value = Presets.Value.FirstOrDefault(x => x.Id == generation?.PresetId);
        _editingSubscription.Disposable = controller.Generation.GetObservable().Subscribe(value =>
            GenerationDetails.Value = value?.Describe() ?? "この要素には生成時設定が保存されていません。");
        if (generation != null && ApplySettings(generation.Settings) && Text.Value == generation.Text)
        {
            try
            {
                AudioQuery.Value = JsonSerializer.Deserialize<AudioQueryModel>(generation.AudioQueryJson);
                HasAudioQuery.Value = AudioQuery.Value != null;
            }
            catch (JsonException)
            {
                // Older or incomplete query data can be analyzed again from the saved text.
                InvalidateQuery();
            }
        }
    }

    public void ClearEditingSpeech()
    {
        _editingSubscription.Disposable = null;
        _editingElement = null;
        HasEditingElement.Value = false;
        EditingElementName.Value = "";
        GenerationDetails.Value = "";
    }

    public Task Regenerate() => _editingElement is { } element ? GenerateCore(element) : Task.CompletedTask;
}
