using Beutl.Extensions.Voice.Models;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed class ScriptSpeakerViewModel
{
    public ScriptSpeakerViewModel(string name, VoicePreset[] presets)
    {
        Name = name;
        Presets.Value = presets;
    }

    public string Name { get; }
    public string DisplayName => Name.Length == 0 ? "話者未指定" : Name;
    public ReactivePropertySlim<VoicePreset[]> Presets { get; } = new([]);
    public ReactivePropertySlim<VoicePreset?> SelectedPreset { get; } = new();

    internal void SetPresets(VoicePreset[] presets)
    {
        var id = SelectedPreset.Value?.Id;
        Presets.Value = presets;
        SelectedPreset.Value = presets.FirstOrDefault(x => x.Id == id);
    }
}
