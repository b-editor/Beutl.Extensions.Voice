using System.Runtime.CompilerServices;
using Beutl.Engine;
using Beutl.Extensions.Voice.Services;
using Beutl.ProjectSystem;
using Beutl.Serialization;

namespace Beutl.Extensions.Voice.Operators;

public partial class TtsController : ISplittable
{
    private static readonly ConditionalWeakTable<JsonSerializationContext, Dictionary<Guid, Guid>> s_copyGroups = new();
    private Guid _groupBeforeCopy;
    private List<WeakReference<EngineObject>>? _copiedTargets;
    private Guid[] _targetIds = [];

    public override void Serialize(ICoreSerializationContext context)
    {
        base.Serialize(context);
        if (LipSyncGroupId.Value == Guid.Empty) return;
        var targets = this.FindHierarchicalParent<Scene>() is { } scene
            ? PsdLipSync.Targets(scene).Where(target => PsdLipSync.GroupOf(target) == LipSyncGroupId.Value)
                .Select(target => target.Id.Value).ToArray()
            : _targetIds;
        // Clipboard elements are deserialized without their scene before the editor copies them.
        context.SetValue("LipSyncTargets", targets);
        if (context.Mode != CoreSerializationMode.EmbedReferencedObjects) return;

        // The editor remaps IDs of objects included in a copy. Keep the original IDs as N-format
        // keys (which it does not remap) to distinguish a copied portrait from an external reference.
        context.SetValue("LipSyncCopyTargets", targets.ToDictionary(id => id.ToString("N"), id => id));
    }

    public override void Deserialize(ICoreSerializationContext context)
    {
        base.Deserialize(context);
        _copiedTargets = null;
        _groupBeforeCopy = LipSyncGroupId.Value;
        _targetIds = context.GetValue<Guid[]>("LipSyncTargets") ?? [];
        var targets = context.GetValue<Dictionary<string, Guid>>("LipSyncCopyTargets");
        if (_groupBeforeCopy == Guid.Empty || targets == null || context is not JsonSerializationContext json) return;
        var copiedIds = targets.Where(pair => Guid.TryParseExact(pair.Key, "N", out var original) && original != pair.Value)
            .Select(pair => pair.Value).ToArray();
        if (copiedIds.Length == 0) return;

        // Every voice and every split part in this copy must receive the same fresh group.
        // Keep this scoped to deserialization: loading the same clipboard again makes another group.
        var groups = s_copyGroups.GetValue(json.Root, _ => new());
        if (!groups.TryGetValue(_groupBeforeCopy, out var group)) groups[_groupBeforeCopy] = group = Guid.NewGuid();
        foreach (var id in copiedIds)
            context.Resolve(id, obj =>
            {
                if (obj is not EngineObject target) return;
                var current = PsdLipSync.GroupOf(target);
                if (current != _groupBeforeCopy && current != group) return;
                PsdLipSync.RebindGroup(target, group);
                LipSyncGroupId.Value = group;
                (_copiedTargets ??= []).Add(new(target));
            });
    }

    public void NotifySplitted(bool backward, MediaTime startDelta, MediaTime durationDelta)
    {
        // Split also uses ObjectRegenerator. When voice and portrait share an element,
        // restore their shared group instead of turning the backward half into a new pair.
        if (!backward || _copiedTargets == null) return;
        LipSyncGroupId.Value = _groupBeforeCopy;
        foreach (var reference in _copiedTargets)
            if (reference.TryGetTarget(out var target)) PsdLipSync.RebindGroup(target, _groupBeforeCopy);
        _copiedTargets = null;
    }
}
