using Beutl.Animation;
using Beutl.Audio;
using Beutl.Engine;
using Beutl.Extensions.Voice.Operators;
using Beutl.Media;
using Beutl.Media.Source;
using Beutl.ProjectSystem;

namespace Beutl.Extensions.Voice.Services;

internal static class PsdLipSync
{
    // Use the shared property contract without loading or redistributing the optional extension.
    public static bool TryGetProperties(EngineObject target, out IExpressionProperty<SoundSource?> audio,
        out IExpressionProperty<MediaTime> offset)
    {
        audio = target.Properties.OfType<IExpressionProperty<SoundSource?>>()
            .FirstOrDefault(x => x.Name == "LipSyncAudio")!;
        offset = target.Properties.OfType<IExpressionProperty<MediaTime>>()
            .FirstOrDefault(x => x.Name == "LipSyncOffset")!;
        return target.GetType().FullName == "Beutl.Extensions.PsdTachie.Graphics.PsdTachieDrawable"
            && audio != null && offset != null;
    }

    public static IEnumerable<EngineObject> Targets(Scene scene) =>
        scene.EnumerateAllChildren<EngineObject>().Where(x => TryGetProperties(x, out _, out _));

    public static string TargetName(EngineObject target)
    {
        var element = target.FindHierarchicalParent<Element>();
        string name = string.IsNullOrWhiteSpace(element?.Name.Value) ? "PSD立ち絵" : element.Name.Value;
        return element == null ? name
            : $"{name} (レイヤー {element.ZIndex.Value + 1}, {element.Start.Value.ToSeconds():0.###}–{element.Range.End.ToSeconds():0.###} 秒)";
    }

    public static void ValidateTarget(Scene scene, EngineObject target)
    {
        if (target.FindHierarchicalParent<Scene>() != scene || target.FindHierarchicalParent<Element>() is not { } element
            || !TryGetProperties(target, out var audio, out var offset))
            throw new InvalidOperationException("連携先のPSD立ち絵が見つかりません。");
        // Joining an existing group changes only the voice, so a locked character can still be reused.
        bool alreadyLinked = audio.Expression is VoiceLipSyncAudioExpression { GroupId: var group } && group != Guid.Empty
            && offset.Expression is VoiceLipSyncOffsetExpression offsetExpression && offsetExpression.GroupId == group;
        if (!alreadyLinked && scene.IsElementLocked(element))
            throw new InvalidOperationException("立ち絵の要素またはレイヤーがロックされています。");
    }

    public static Guid GroupOf(EngineObject target)
    {
        if (!TryGetProperties(target, out var audio, out var offset)) return Guid.Empty;
        return audio.Expression is VoiceLipSyncAudioExpression audioExpression ? audioExpression.GroupId
            : offset.Expression is VoiceLipSyncOffsetExpression offsetExpression ? offsetExpression.GroupId : Guid.Empty;
    }

    public static void Connect(TtsController controller, EngineObject target, bool extendToSpeech = false)
    {
        var scene = controller.FindHierarchicalParent<Scene>()
            ?? throw new InvalidOperationException("編集する読み上げ要素が見つかりません。");
        ValidateTarget(scene, target);
        TryGetProperties(target, out var audio, out var offset);
        var group = GroupOf(target);
        if (controller.LipSyncGroupId.Value != group)
            Disconnect(controller, scene);
        if (group == Guid.Empty) group = Guid.NewGuid();
        if (audio.Expression is not VoiceLipSyncAudioExpression)
            audio.Expression = new VoiceLipSyncAudioExpression
            {
                GroupId = group, PreviousExpression = audio.SerializeExpression(), ExtendToSpeech = extendToSpeech
            };
        if (offset.Expression is not VoiceLipSyncOffsetExpression { GroupId: var offsetGroup } || offsetGroup != group)
            offset.Expression = new VoiceLipSyncOffsetExpression { GroupId = group, PreviousExpression = offset.SerializeExpression() };
        controller.LipSyncGroupId.Value = group;
    }

    public static void Disconnect(TtsController controller, Scene scene)
    {
        var targets = TargetsToRestore(controller, scene).ToArray();
        if (targets.Any(x => x.FindHierarchicalParent<Element>() is { } element && scene.IsElementLocked(element)))
            throw new InvalidOperationException("立ち絵の要素またはレイヤーがロックされています。");
        Guid group = controller.LipSyncGroupId.Value;
        controller.LipSyncGroupId.Value = Guid.Empty;
        foreach (var target in targets)
        {
            // Removing the last voice restores the original inputs, including on split characters.
            TryGetProperties(target, out var audio, out var offset);
            if (audio.Expression is VoiceLipSyncAudioExpression audioExpression && audioExpression.GroupId == group)
                audio.Expression = audioExpression.Replaced;
            if (offset.Expression is VoiceLipSyncOffsetExpression expression && expression.GroupId == group)
                offset.Expression = expression.Replaced;
        }
    }

    private static IEnumerable<EngineObject> TargetsToRestore(TtsController controller, Scene scene)
    {
        Guid group = controller.LipSyncGroupId.Value;
        return group == Guid.Empty || scene.EnumerateAllChildren<TtsController>()
            .Any(x => x != controller && x.LipSyncGroupId.Value == group) ? [] : Targets(scene).Where(x => GroupOf(x) == group);
    }

    public static SourceSound? FindSound(ExpressionContext context, Guid group)
    {
        if (group == Guid.Empty || context.CurrentProperty.GetOwnerObject() is not EngineObject target
            || target.FindHierarchicalParent<Scene>() is not { } scene) return null;
        bool hasSolo = scene.Layers.Any(x => x.IsSolo.Value);
        // Only one input fits PsdTachie's audio slot. In an overlap, the most recently started line wins.
        return scene.Children.Where(x => x.IsEnabled.Value && x.Range.Contains(context.Time) && IsAudibleLayer(x.ZIndex.Value)
                && x.Objects.OfType<TtsController>().Any(c => c.LipSyncGroupId.Value == group))
            .OrderByDescending(x => x.Start.Value).ThenBy(x => x.ZIndex.Value)
            .SelectMany(x => x.Objects.OfType<SourceSound>())
            .FirstOrDefault(x => x.IsEnabled.Value && x.TimeRange.Value.Contains(context.Time));

        bool IsAudibleLayer(int zIndex)
        {
            var layer = scene.Layers.FirstOrDefault(x => x.ZIndex.Value == zIndex);
            return (!hasSolo || layer?.IsSolo.Value == true) && layer?.IsAudioMuted.Value != true;
        }
    }

    public static MediaTime SourceTime(SourceSound sound, MediaTime time, Func<KeyFrameAnimation<float>, SpeedIntegrator> getIntegrator)
    {
        MediaTime local = time - sound.TimeRange.Value.Start;
        MediaTime mapped;
        if (sound.Speed.GetKeyFrameAnimation() is { } animation)
        {
            var integrator = getIntegrator(animation);
            lock (integrator)
                mapped = animation.UseGlobalClock.Value
                    ? integrator.Integrate(time, animation) - integrator.Integrate(sound.TimeRange.Value.Start, animation)
                    : integrator.Integrate(local, animation);
        }
        else mapped = MediaTime.FromSeconds(local.ToSeconds() * sound.Speed.Value / 100d);
        return mapped + sound.OffsetPosition.Value;
    }
}
