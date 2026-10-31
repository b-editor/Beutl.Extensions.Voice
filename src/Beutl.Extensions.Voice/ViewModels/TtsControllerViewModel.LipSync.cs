using System.Reactive.Disposables;
using Beutl.Engine;
using Beutl.Extensions.Voice.Services;
using Beutl.ProjectSystem;
using Reactive.Bindings;

namespace Beutl.Extensions.Voice.ViewModels;

public sealed record LipSyncTarget(EngineObject Object, string Name);

public partial class TtsControllerViewModel
{
    private readonly SerialDisposable _lipSyncHistory = new();

    public ReactivePropertySlim<LipSyncTarget[]> LipSyncTargets { get; } = new([]);
    public ReactivePropertySlim<LipSyncTarget?> SelectedLipSyncTarget { get; } = new();
    public ReactivePropertySlim<bool> HasLipSync { get; } = new();
    public ReactivePropertySlim<bool> HasLipSyncTargets { get; } = new();

    public void RefreshLipSyncTargets()
    {
        if (_disposed) return;
        var scene = _controller.FindHierarchicalParent<Scene>();
        var previous = SelectedLipSyncTarget.Value?.Object;
        var group = _controller.LipSyncGroupId.Value;
        HasLipSync.Value = group != Guid.Empty;
        LipSyncTargets.Value = scene == null ? [] : PsdLipSync.Targets(scene)
            .Select(target => new LipSyncTarget(target, PsdLipSync.TargetName(target))).ToArray();
        HasLipSyncTargets.Value = LipSyncTargets.Value.Length > 0;
        SelectedLipSyncTarget.Value = LipSyncTargets.Value.FirstOrDefault(x => group != Guid.Empty && PsdLipSync.GroupOf(x.Object) == group)
            ?? LipSyncTargets.Value.FirstOrDefault(x => x.Object == previous);
    }

    public void ConnectLipSync()
    {
        if (IsGenerating.Value || _disposed) return;
        try
        {
            GetEditableTarget();
            if (SelectedLipSyncTarget.Value?.Object is not { } target)
                throw new InvalidOperationException("連携するPSD立ち絵を選択してください。");
            _history!.ExecuteInTransaction(() => PsdLipSync.Connect(_controller, target));
            RefreshLipSyncTargets();
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
            RefreshLipSyncTargets();
            Message.Value = "口パクの連携を解除しました。";
        }
        catch (Exception ex) { Message.Value = ex.Message; }
    }
}
