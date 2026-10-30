using Beutl.Extensions.Voice.Models;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed class ScriptSpeakerViewModel : VoiceEditorViewModel
{
    public ScriptSpeakerViewModel(string name, VoicePreset[] presets)
    {
        Name = name;
        Presets.Value = presets;
        OnLoaded();
    }

    public string Name { get; }
    public string DisplayName => Name.Length == 0 ? "話者未指定" : Name;
    public ReactivePropertySlim<VoicePreset[]> Presets { get; } = new([]);
    public ReactivePropertySlim<VoicePreset?> SelectedPreset { get; } = new();
    public VoicePreset? AppliedPreset { get; private set; }
    internal VoiceSettings? Settings => CaptureStoredSettings();
    internal VoiceSettings? AvailableSettings => CaptureSettings();

    public void ApplyPreset()
    {
        if (SelectedPreset.Value is not { } preset) return;
        ApplySettings(preset.Settings);
        AppliedPreset = preset;
    }

    internal void Restore(VoiceSettings? settings, VoicePreset? preset)
    {
        RestoreState("", settings, null);
        AppliedPreset = preset;
        SelectedPreset.Value = Presets.Value.FirstOrDefault(x => x.Id == preset?.Id);
    }

    internal void SetPresets(VoicePreset[] presets)
    {
        var id = SelectedPreset.Value?.Id;
        Presets.Value = presets;
        SelectedPreset.Value = presets.FirstOrDefault(x => x.Id == id);
    }
}
