using Beutl.Extensions.Voice.Models;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed class ScriptSpeakerViewModel
{
    private bool _refreshingPresets;

    public ScriptSpeakerViewModel(string name, VoicePreset[] presets, VoicePreset? assignedPreset = null)
    {
        Name = name;
        SelectedPreset.Subscribe(preset =>
        {
            if (!_refreshingPresets) AssignedPreset = preset;
        });
        AssignedPreset = assignedPreset;
        SetPresets(presets);
    }

    public string Name { get; }
    public string DisplayName => Name.Length == 0 ? "話者未指定" : Name;
    public ReactivePropertySlim<VoicePreset[]> Presets { get; } = new([]);
    public ReactivePropertySlim<VoicePreset?> SelectedPreset { get; } = new();
    internal VoicePreset? AssignedPreset { get; private set; }

    internal void SetPresets(VoicePreset[] presets)
    {
        _refreshingPresets = true;
        try
        {
            Presets.Value = presets;
            SelectedPreset.Value = presets.FirstOrDefault(x => x.Id == AssignedPreset?.Id);
            // Keep unavailable assignments for a later refresh, but only offer loaded presets for generation.
            AssignedPreset = SelectedPreset.Value ?? AssignedPreset;
        }
        finally { _refreshingPresets = false; }
    }
}
