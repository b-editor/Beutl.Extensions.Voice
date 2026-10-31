using Beutl.Engine;
using Beutl.Extensions.Voice.Models;
using Beutl.Extensions.Voice.Services;
using Beutl.ProjectSystem;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed record LipSyncChoice(Guid? Id, string Name, VoiceLipSyncPreset? Preset = null);

public sealed class LipSyncSelectionViewModel
{
    private readonly Scene _scene;

    public LipSyncSelectionViewModel(Scene scene)
    {
        _scene = scene;
        Refresh();
    }

    public ReactivePropertySlim<LipSyncChoice[]> Targets { get; } = new([]);
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
        List<LipSyncChoice> targets = [new(null, "連携しない")];
        if (preset != null) targets.Add(new(null, $"プリセット: {Path.GetFileNameWithoutExtension(preset.PsdPath)}", preset));
        targets.AddRange(PsdLipSync.Targets(_scene).Select(x => new LipSyncChoice(x.Id.Value, PsdLipSync.TargetName(x))));
        // Keep saved choices until their scene objects load or are restored by Undo.
        if (selectedId != null && targets.All(x => x.Id != selectedId))
            targets.Add(new(selectedId, "立ち絵が見つかりません"));
        Targets.Value = targets.ToArray();
        Selected.Value = targets.First(x => x.Id == selectedId && x.Preset == preset);
    }

    internal VoiceLipSyncPreset? CapturePreset() => Preset ?? (FindTarget() is { } target ? PsdLipSyncPresets.Capture(target) : null);

    internal EngineObject? CaptureTarget()
    {
        var target = FindTarget();
        if (target != null) PsdLipSync.ValidateTarget(_scene, target);
        return target;
    }

    private EngineObject? FindTarget()
    {
        if (SelectedId is not { } id) return null;
        return PsdLipSync.Targets(_scene).FirstOrDefault(x => x.Id.Value == id)
            ?? throw new InvalidOperationException("連携先のPSD立ち絵が見つかりません。立ち絵を選び直してください。");
    }
}
