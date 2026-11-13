using System.Reactive.Disposables;
using Beutl.Editor.Services;
using Beutl.Extensions.Voice.Services;
using Beutl.ProjectSystem;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

// The character comes from a saved template, as when the speech is generated: linking reuses the scene's character
// showing the same PSD, or adds the template's. The status names the character the speech moves now.
public partial class TtsControllerViewModel
{
    private readonly SerialDisposable _lipSyncHistory = new();
    private readonly Func<IEnumerable<ObjectTemplateItem>>? _loadTemplates;

    public ReactivePropertySlim<LipSyncChoice[]> LipSyncTemplates { get; } = new([]);
    public ReactivePropertySlim<LipSyncChoice?> SelectedLipSyncTemplate { get; } = new();
    public ReactivePropertySlim<bool> HasLipSync { get; } = new();
    public ReactivePropertySlim<bool> HasLipSyncTemplates { get; } = new();
    public ReactivePropertySlim<string> LipSyncStatus { get; } = new("");

    public void RefreshLipSyncTemplates()
    {
        if (_disposed) return;
        var selected = SelectedLipSyncTemplate.Value?.Id;
        LipSyncTemplates.Value = PsdLipSyncPresets.CharacterTemplates((_loadTemplates ?? VoiceTemplates.Load)())
            .Select(x => new LipSyncChoice(x.Id, x.Name.Value)).ToArray();
        HasLipSyncTemplates.Value = LipSyncTemplates.Value.Length > 0;
        SelectedLipSyncTemplate.Value = LipSyncTemplates.Value.FirstOrDefault(x => x.Id == selected);
    }

    private void RefreshLipSyncStatus()
    {
        if (_disposed) return;
        var group = _controller.LipSyncGroupId.Value;
        HasLipSync.Value = group != Guid.Empty;
        var characters = group == Guid.Empty || _controller.FindHierarchicalParent<Scene>() is not { } scene ? []
            : PsdLipSync.Targets(scene).Where(x => PsdLipSync.GroupOf(x) == group).ToArray();
        LipSyncStatus.Value = group == Guid.Empty ? "立ち絵と連携していません。"
            : characters.Length == 0 ? "連携中の立ち絵がシーンにありません。"
            // A split character has several parts; name the one this speech moves.
            : $"連携中: {PsdLipSync.TargetName(PsdLipSyncPresets.SelectTarget(characters, _controller.TimeRange.Value))}";
    }

    public void ConnectLipSync()
    {
        if (IsGenerating.Value || _disposed) return;
        try
        {
            var (_, scene) = GetEditableTarget();
            if (SelectedLipSyncTemplate.Value?.Id is not { } id)
                throw new InvalidOperationException("連携する立ち絵のテンプレートを選択してください。");
            var preset = PsdLipSyncPresets.Capture((_loadTemplates ?? VoiceTemplates.Load)(), id);
            _history!.ExecuteInTransaction(() => PsdLipSyncPresets.Connect(scene, [(_controller, preset)]));
            RefreshLipSyncStatus();
            Message.Value = "立ち絵を読み上げ音声に連携しました。";
        }
        catch (Exception ex) { Message.Value = ex.Message; }
    }

    public void DisconnectLipSync()
    {
        if (IsGenerating.Value || _disposed) return;
        try
        {
            var (_, scene) = GetEditableTarget();
            _history!.ExecuteInTransaction(() => PsdLipSync.Disconnect(_controller, scene));
            RefreshLipSyncStatus();
            Message.Value = "口パクの連携を解除しました。";
        }
        catch (Exception ex) { Message.Value = ex.Message; }
    }
}
