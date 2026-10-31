using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

namespace Beutl.Extensions.Voice.ViewModels;

public partial class TtsTabViewModel
{
    // Applying or saving the current settings establishes their source; browsing does not.
    private VoicePreset? _appliedPreset;

    // Keep item replacement and selection notifications in order on the UI thread.
    public ReactivePropertySlim<VoicePreset[]> Presets { get; } = new([]);
    public ReactivePropertySlim<VoicePreset?> SelectedPreset { get; } = new();
    public ReactiveProperty<string> PresetName { get; } = new("");
    public ReactiveProperty<string> PresetDetails { get; } = new("");
    public ReactivePropertySlim<string> PresetMessage => Message;
    public ReactiveProperty<bool> HasSelectedPreset { get; } = new();

    private void InitializePresets()
    {
        RefreshPresets();
        SelectedPreset.Subscribe(preset =>
        {
            PresetName.Value = preset?.Name ?? "";
            HasSelectedPreset.Value = preset != null;
            RefreshPresetDetails();
        }).AddTo(_subscriptions);
        SelectedVoice.Subscribe(_ => RefreshPresetDetails()).AddTo(_subscriptions);
        SelectedStyle.Subscribe(_ => RefreshPresetDetails()).AddTo(_subscriptions);
        SelectedEngine.Subscribe(_ => RefreshPresetDetails()).AddTo(_subscriptions);
        LipSync.Selected.Subscribe(_ => RefreshPresetDetails()).AddTo(_subscriptions);
        foreach (var property in new[] { SpeedScale, PitchScale, IntonationScale, VolumeScale, PrePhonemeLength, PostPhonemeLength, TempoDynamicsScale })
            property.Subscribe(_ => RefreshPresetDetails()).AddTo(_subscriptions);
    }

    public void RefreshPresets()
    {
        var id = SelectedPreset.Value?.Id;
        Presets.Value = [];
        SelectedPreset.Value = null;
        try
        {
            Presets.Value = VoicePresetStore.Load(VoicePresetStore.GlobalPath);
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
        var preset = new VoicePreset { Name = RequirePresetName(), Settings = settings, LipSync = LipSync.CapturePreset() };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
        return preset.Id;
    }, capturesCurrentSettings: true);

    public void DuplicatePreset() => EditPreset(() =>
    {
        var preset = RequirePreset();
        var duplicate = preset with { Id = Guid.NewGuid(), Name = preset.Name + " のコピー" };
        VoicePresetStore.Save(VoicePresetStore.GlobalPath, duplicate);
        return duplicate.Id;
    });

    public void RenamePreset() => EditPreset(() =>
    {
        var id = RequirePreset().Id;
        string name = RequirePresetName();
        VoicePresetStore.Update(VoicePresetStore.GlobalPath, id, preset => preset with { Name = name });
        return id;
    });

    public void UpdatePreset() => EditPreset(() =>
    {
        var id = RequirePreset().Id;
        var settings = CaptureSettings() ?? throw new InvalidOperationException("話者とスタイルを選択してください。");
        var lipSync = LipSync.CapturePreset();
        VoicePresetStore.Update(VoicePresetStore.GlobalPath, id, preset => preset with { Settings = settings, LipSync = lipSync });
        return id;
    }, capturesCurrentSettings: true);

    private VoicePreset RequirePreset() => SelectedPreset.Value
        ?? throw new InvalidOperationException("プリセットを選択してください。");

    private string RequirePresetName() => string.IsNullOrWhiteSpace(PresetName.Value)
        ? throw new InvalidOperationException("プリセット名を入力してください。") : PresetName.Value.Trim();

    private void EditPreset(Func<Guid> edit, bool capturesCurrentSettings = false)
    {
        try
        {
            var id = edit();
            RefreshPresets();
            var preset = Presets.Value.First(x => x.Id == id);
            SelectedPreset.Value = preset;
            if (capturesCurrentSettings)
            {
                _appliedPreset = preset;
                LipSync.Preset = preset.LipSync;
            }
            else if (_appliedPreset is { } applied && applied.Id == id)
                _appliedPreset = applied with { Name = preset.Name };
            PresetMessage.Value = "プリセットを保存しました。";
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
            VoicePresetStore.Delete(VoicePresetStore.GlobalPath, RequirePreset().Id);
            RefreshPresets();
            PresetMessage.Value = "プリセットを削除しました。";
        }
        catch (Exception ex)
        {
            PresetMessage.Value = $"プリセットを削除できませんでした: {ex.Message}";
        }
    }

    public void ApplyPreset()
    {
        if (SelectedPreset.Value is { } preset)
        {
            ApplySettings(preset.Settings);
            LipSync.Preset = preset.LipSync;
            _appliedPreset = preset;
        }
        RefreshPresetDetails();
    }

    private void RefreshPresetDetails()
    {
        var preset = SelectedPreset.Value;
        PresetDetails.Value = preset == null ? "" : preset.Settings.Describe() +
            (preset.LipSync is { } lipSync ? $"\n口パク: {Path.GetFileNameWithoutExtension(lipSync.PsdPath)}" : "") + "\n\n" +
            (CaptureSettings() is { } current ? preset.Settings.DescribeDifferences(current) : "現在の話者が未選択です。") +
            // A scene target is an explicit choice; a saved preset can also create or reuse a character.
            (LipSync.SelectedId != null || LipSync.Preset != preset.LipSync
                ? $"\n口パクの変更: {(preset.LipSync is { } saved ? Path.GetFileNameWithoutExtension(saved.PsdPath) : "連携しない")} → {LipSync.Selected.Value?.Name ?? "連携しない"}"
                : "");
    }
}
