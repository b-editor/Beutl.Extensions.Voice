using Beutl.Editor.Services;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed record LipSyncChoice(Guid? Id, string Name, VoiceLipSyncPreset? Preset = null);

// The character comes from a saved template, as a caption does; characters on the timeline are not offered.
// Placement links the scene's character showing the same PSD, or adds the template's.
public sealed class LipSyncSelectionViewModel
{
    private readonly Func<IEnumerable<ObjectTemplateItem>>? _loadTemplates;

    public LipSyncSelectionViewModel(Func<IEnumerable<ObjectTemplateItem>>? loadTemplates = null)
    {
        _loadTemplates = loadTemplates;
        Refresh();
    }

    public ReactivePropertySlim<LipSyncChoice[]> Choices { get; } = new([]);
    public ReactivePropertySlim<LipSyncChoice?> Selected { get; } = new();

    public Guid? SelectedId
    {
        get => Selected.Value?.Id;
        set => Refresh(value, null);
    }

    public VoiceLipSyncPreset? Preset
    {
        get => Selected.Value?.Preset;
        set => Refresh(null, value);
    }

    public void Refresh() => Refresh(SelectedId, Preset);

    private void Refresh(Guid? selectedId, VoiceLipSyncPreset? preset)
    {
        List<LipSyncChoice> choices = [new(null, "連携しない")];
        if (preset != null) choices.Add(new(null, $"プリセット: {Path.GetFileNameWithoutExtension(preset.PsdPath)}", preset));
        choices.AddRange(PsdLipSyncPresets.CharacterTemplates(GetTemplates()).Select(x => new LipSyncChoice(x.Id, x.Name.Value)));
        if (selectedId != null && choices.All(x => x.Id != selectedId)) choices.Add(new(selectedId, "テンプレートを使用できません"));
        Choices.Value = choices.ToArray();
        Selected.Value = choices.First(x => x.Id == selectedId && x.Preset == preset);
    }

    internal VoiceLipSyncPreset? CapturePreset() =>
        Preset ?? (SelectedId is { } id ? PsdLipSyncPresets.Capture(GetTemplates(), id) : null);

    private IEnumerable<ObjectTemplateItem> GetTemplates() => (_loadTemplates ?? VoiceTemplates.Load)();
}
