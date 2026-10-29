using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;

namespace Beutl.Extensions.Voice.ViewModels;

public partial class TtsTabViewModel
{
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
        foreach (var property in new[] { SpeedScale, PitchScale, IntonationScale, VolumeScale, PrePhonemeLength, PostPhonemeLength })
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
            VoicePresetStore.Save(VoicePresetStore.GlobalPath, preset);
            RefreshPresets();
            SelectedPreset.Value = Presets.Value.First(x => x.Id == preset.Id);
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
        if (SelectedPreset.Value is { } preset) ApplySettings(preset.Settings);
        RefreshPresetDetails();
    }

    private void RefreshPresetDetails()
    {
        var preset = SelectedPreset.Value;
        PresetDetails.Value = preset == null ? "" : preset.Settings.Describe() + "\n\n" +
            (CaptureSettings() is { } current ? preset.Settings.DescribeDifferences(current) : "現在の話者が未選択です。");
    }
}
